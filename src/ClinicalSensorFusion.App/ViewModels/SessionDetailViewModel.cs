using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

public sealed record SummaryGroup(string Section, IReadOnlyList<SummaryRow> Rows);

public sealed record AudioItem(string Id, string Title, string Details, string Path);

public sealed record AnalysisItem(string When, string Model, string Response);

/// <summary>Stato dell'invio di una rilevazione al server clinico, come lo vede l'operatore.</summary>
public enum SyncState
{
    /// <summary>Rilevazione incompleta: niente da inviare.</summary>
    Unavailable,

    /// <summary>Server o chiave del dispositivo non impostati.</summary>
    NotConfigured,
    Pending,
    Sending,
    Sent,
}

/// <summary>Dettaglio di una rilevazione: indicatori, audio, analisi con il modello linguistico.</summary>
/// <remarks>
/// Volutamente non IDisposable: è registrato come transient e il container DI di MAUI trattiene fino alla
/// chiusura dell'app i transient disposable (memory leak a ogni navigazione). Il CancellationTokenSource
/// viene rilasciato nel <c>finally</c> dell'analisi.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Vedi remarks: CTS rilasciato nel finally.")]
public sealed partial class SessionDetailViewModel(
    ClinicalRepository repository,
    OllamaClient ollama,
    IAppSettings settings,
    IAudioPlayback playback,
    SyncService sync,
    IDialogService dialogs) : ObservableObject, IQueryAttributable
{
    /// <summary>Parametro di navigazione: la pagina si apre subito dopo la fine di un'acquisizione.</summary>
    public const string JustRecordedKey = "JustRecorded";

    private ClinicalSession? _session;
    private CancellationTokenSource? _analysisCts;

    public ObservableCollection<SummaryGroup> Summary { get; } = [];

    public ObservableCollection<AudioItem> Audio { get; } = [];

    public ObservableCollection<AnalysisItem> Analyses { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CaptureMode Mode { get; set; }

    [ObservableProperty]
    public partial string ModeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WhenText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DurationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OperatorName { get; set; } = string.Empty;

    /// <summary>Esito del test standardizzato, se la rilevazione ne ha eseguito uno.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTest), nameof(TestName), nameof(TestValue), nameof(TestInterpretation), nameof(TestFlag), nameof(TestIsOk), nameof(TestEstimated), nameof(TestReference), nameof(TestSource))]
    public partial TestOutcome? TestResult { get; set; }

    public bool HasTest => TestResult is not null;

    // Nome e interpretazione sono salvati nel riepilogo: il nome in italiano (si traduce qui), l'interpretazione
    // nella lingua che l'interfaccia aveva al momento della rilevazione.
    public string TestName => TestResult is { } result ? Loc.T(result.TestName) : string.Empty;

    public string TestValue => TestResult?.Format(CultureInfo.CurrentCulture) ?? string.Empty;

    public string TestInterpretation => TestResult?.Interpretation ?? string.Empty;

    public bool TestFlag => TestResult?.Flag == true;

    public bool TestIsOk => TestResult is { Flag: false };

    public bool TestEstimated => TestResult?.Estimated == true;

    public string TestReference => ClinicalTests.Find(TestResult?.TestId)?.Localized().Reference ?? string.Empty;

    public string TestSource => ClinicalTests.Find(TestResult?.TestId)?.Localized().Source ?? string.Empty;

    /// <summary>Falso se la rilevazione è stata interrotta prima del calcolo degli indicatori.</summary>
    [ObservableProperty]
    public partial bool HasSummary { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    public partial string? Question { get; set; }

    [ObservableProperty]
    public partial string AnalysisText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAnalysisCommand))]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSyncAvailable), nameof(IsSyncNotConfigured), nameof(IsSyncPending), nameof(IsSyncSending), nameof(IsSyncSent))]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    public partial SyncState SyncState { get; set; }

    /// <summary>Riga sotto il titolo della scheda: quando è stata inviata, oppure cosa succede inviandola.</summary>
    [ObservableProperty]
    public partial string SyncText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SyncError { get; set; }

    public bool IsSyncAvailable => SyncState != SyncState.Unavailable;

    public bool IsSyncNotConfigured => SyncState == SyncState.NotConfigured;

    public bool IsSyncPending => SyncState == SyncState.Pending;

    public bool IsSyncSending => SyncState == SyncState.Sending;

    public bool IsSyncSent => SyncState == SyncState.Sent;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("SessionId", out var id) && id is string sessionId)
        {
            var justRecorded = query.TryGetValue(JustRecordedKey, out var flag) && flag is true;
            _ = LoadAsync(sessionId, justRecorded);
        }
    }

    private async Task LoadAsync(string id, bool justRecorded)
    {
        try
        {
            _session = await repository.GetSessionAsync(id);
            if (_session is null)
            {
                Title = Loc.T("Rilevazione non trovata");
                return;
            }

            var c = CultureInfo.CurrentCulture;
            Title = _session.SubjectCode;
            Mode = _session.Mode;
            var mode = Loc.T(ClinicalPromptBuilder.Describe(_session.Mode));
            ModeText = mode.Length > 0 ? char.ToUpper(mode[0], c) + mode[1..] : mode;
            WhenText = _session.StartedAt.ToLocalTime().ToString("f", c);
            DurationText = _session.Duration is { } d ? d.ToString(@"mm\:ss", c) : Loc.T("incompleta");
            OperatorName = _session.OperatorName;
            HasSummary = _session.Summary is not null;
            TestResult = _session.Summary?.Test;
            Notes = _session.Notes;

            Summary.Clear();
            if (_session.Summary is { } summary)
            {
                // Prima degli indicatori: quanto ci si può fidare della misura e del documento che parte per il server.
                Summary.Add(new SummaryGroup(MeasurementReliability.Section, MeasurementReliability.Assess(summary, c).Factors));
                var payload = await sync.InspectPayloadAsync(_session);
                Summary.Add(new SummaryGroup(ClinicalSensorFusion.Core.Sync.PayloadReport.Section, payload.ToRows(c)));

                foreach (var group in SummaryReport.Build(summary, c).GroupBy(r => r.Section))
                {
                    Summary.Add(new SummaryGroup(group.Key, [.. group]));
                }
            }

            Audio.Clear();
            foreach (var a in await repository.ListAudioRecordingsAsync(id))
            {
                var details = a.Features is { } f
                    ? string.Format(c, Loc.T("{0:F1} s · RMS {1:F1} dBFS · attivo {2:P0}"), a.DurationSeconds, f.RmsDbfs, f.ActiveRatio)
                    : string.Format(c, "{0:F1} s", a.DurationSeconds);
                Audio.Add(new AudioItem(a.Id, "audio.caf (48 kHz, Float32)", details, AppPaths.Audio(a)));
            }

            await LoadAnalysesAsync();
            RefreshSyncState();

            // A fine acquisizione l'invio parte da solo, se l'operatore lo ha scelto nelle Impostazioni.
            if (justRecorded && sync.AutoSync && SyncState == SyncState.Pending)
            {
                await SendToServerAsync();
            }
        }
        catch (Exception ex)
        {
            await dialogs.AlertAsync(Loc.T("Errore"), ex.Message);
        }
    }

    private void RefreshSyncState()
    {
        SyncError = null;
        if (_session?.EndedAt is null)
        {
            SyncState = SyncState.Unavailable;
        }
        else if (!sync.IsConfigured)
        {
            SyncState = SyncState.NotConfigured;
            SyncText = Loc.T("Imposta indirizzo e chiave del dispositivo per inviare la rilevazione e vedere i grafici nella dashboard.");
        }
        else if (_session.SyncedAt is { } at)
        {
            SyncState = SyncState.Sent;
            SyncText = Loc.F("Inviata il {0:g}. I grafici sono nella dashboard.", at.ToLocalTime());
        }
        else
        {
            SyncState = SyncState.Pending;
            SyncText = Loc.T("Non ancora inviata. Il server riceve indicatori, dati dei sensori, audio e analisi e ne ricava i grafici.");
        }
    }

    private bool CanSendToServer() => SyncState is SyncState.Pending or SyncState.Sent;

    /// <summary>Invia (o reinvia) questa rilevazione al server clinico, che ne costruisce i grafici.</summary>
    [RelayCommand(CanExecute = nameof(CanSendToServer))]
    private async Task SendToServerAsync()
    {
        if (_session is null)
        {
            return;
        }

        var previous = SyncState;
        SyncState = SyncState.Sending;
        SyncError = null;
        try
        {
            var report = await sync.SyncSessionAsync(_session.Id);
            if (report.Errors.Count > 0)
            {
                SyncError = string.Join(" ", report.Errors);
                SyncState = previous;
                return;
            }

            _session.SyncedAt = DateTimeOffset.UtcNow;
            RefreshSyncState();
        }
        catch (Exception ex)
        {
            SyncError = ex is OperationCanceledException ? Loc.T("Il server non ha risposto in tempo.") : ex.Message;
            SyncState = previous;
        }
    }

    [RelayCommand]
    private async Task OpenDashboardAsync()
    {
        if (_session is not null && sync.DashboardUrlFor(_session.Id) is { } url)
        {
            await Launcher.Default.OpenAsync(url);
        }
    }

    [RelayCommand]
    private static Task OpenSettingsAsync() => Shell.Current.GoToAsync("//settings");

    private async Task LoadAnalysesAsync()
    {
        Analyses.Clear();
        if (_session is null)
        {
            return;
        }

        foreach (var a in await repository.ListAnalysesAsync(_session.Id))
        {
            Analyses.Add(new AnalysisItem(a.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), $"{a.Model} · {a.Language}", a.Response));
        }
    }

    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (_session?.Summary is null)
        {
            await dialogs.AlertAsync(Loc.T("Analisi"), Loc.T("La rilevazione non ha un riepilogo (forse è stata interrotta)."));
            return;
        }

        if (!OllamaEndpoint.TryParse(settings.OllamaServer, out var server) || server is null)
        {
            await dialogs.AlertAsync(Loc.T("Analisi"), Loc.T("Configurare l'indirizzo del server Ollama nelle Impostazioni."));
            return;
        }

        var messages = ClinicalPromptBuilder.Build(_session, settings.OllamaLanguage, Question, settings.OllamaPrompt, settings.OllamaFhirRelease);
        var prompt = string.Join("\n\n", messages.Select(m => $"[{m.Role}]\n{m.Content}"));
        var text = new StringBuilder();
        _analysisCts = new CancellationTokenSource();
        IsAnalyzing = true;
        AnalysisText = string.Empty;
        try
        {
            await foreach (var part in ollama.ChatStreamAsync(server, settings.OllamaModel, messages, _analysisCts.Token))
            {
                text.Append(part);
                AnalysisText = text.ToString();
            }

            await repository.AddAnalysisAsync(new AiAnalysis
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = _session.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Model = settings.OllamaModel,
                Language = settings.OllamaLanguage,
                OperatorId = settings.OperatorId,
                Prompt = prompt,
                Response = text.ToString(),
            });
            Question = null;
            AnalysisText = string.Empty;
            await LoadAnalysesAsync();
        }
        catch (OperationCanceledException) when (_analysisCts.IsCancellationRequested)
        {
            AnalysisText = text.Append("\n\n").Append(Loc.T("[interrotta]")).ToString();
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException or OperationCanceledException)
        {
            // Timeout, rete caduta o stream troncato: il testo parziale resta visibile ma non viene salvato.
            if (text.Length > 0)
            {
                AnalysisText = text.Append("\n\n").Append(Loc.T("[incompleta: non salvata]")).ToString();
            }

            await dialogs.AlertAsync(
                Loc.T("Analisi non completata"),
                ex is OperationCanceledException ? Loc.T("Il server Ollama non ha risposto in tempo.") : ex.Message);
        }
        finally
        {
            IsAnalyzing = false;
            _analysisCts.Dispose();
            _analysisCts = null;
        }
    }

    private bool CanCancelAnalysis() => IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelAnalysis() => _analysisCts?.Cancel();

    [RelayCommand]
    private async Task PlayAsync(AudioItem item)
    {
        try
        {
            if (playback.IsPlaying)
            {
                playback.StopPlayback();
                return;
            }

            playback.Play(item.Path);
        }
        catch (Exception ex)
        {
            await dialogs.AlertAsync(Loc.T("Riproduzione"), ex.Message);
        }
    }

    /// <summary>
    /// Condivide il solo riepilogo numerico (JSON), senza codice soggetto né nome operatore:
    /// è l'operatore a decidere dove inviarlo.
    /// </summary>
    [RelayCommand]
    private async Task ShareSummaryAsync()
    {
        if (_session?.Summary is null)
        {
            return;
        }

        if (!await dialogs.ConfirmAsync(Loc.T("Condividi riepilogo"), Loc.T("Verrà condiviso solo il riepilogo numerico, senza codice soggetto, operatore né posizione GPS. Procedere?"), Loc.T("Condividi")))
        {
            return;
        }

        var json = JsonSerializer.Serialize(_session.Summary.WithoutLocation(), CsfJsonContext.Indented.SessionSummary);
        await Share.Default.RequestAsync(new ShareTextRequest { Title = Loc.T("Riepilogo rilevazione"), Text = json });
    }
}
