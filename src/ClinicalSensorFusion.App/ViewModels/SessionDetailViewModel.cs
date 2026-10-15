using ClinicalSensorFusion.Core.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using ClinicalSensorFusion.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

public sealed record SummaryGroup(string Section, IReadOnlyList<SummaryRow> Rows);

public sealed record AudioItem(string Id, string Title, string Details, string Path);

public sealed record AnalysisItem(string When, string Model, string Response);

/// <summary>Una misura del soggetto nella scheda del rischio di caduta, con il verso e il peso del suo effetto.</summary>
/// <param name="BarWidth">Lunghezza della barra (punti), proporzionale all'effetto più grande della scheda.</param>
public sealed record FallRiskFactorItem(string Name, string Value, string Effect, Color Color, double BarWidth);

/// <summary>Un test che alimenta il modello: eseguito (con il risultato più recente) o mancante.</summary>
public sealed record FallRiskTestItem(string Name, string Value, bool Done);

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
    MauiAppSettings settings,
    IAudioPlayback playback,
    SyncService sync,
    IDialogService dialogs) : ObservableObject, IQueryAttributable
{
    /// <summary>Parametro di navigazione: la pagina si apre subito dopo la fine di un'acquisizione.</summary>
    public const string JustRecordedKey = "JustRecorded";

    private ClinicalSession? _session;
    private CancellationTokenSource? _analysisCts;

    public ObservableCollection<SummaryGroup> Summary { get; } = [];

    /// <summary>Sensori ambientali, mostrati a parte come schede e nascondibili con l'interruttore.</summary>
    public ObservableCollection<SensorCard> Environment { get; } = [];

    public bool HasEnvironment => Environment.Count > 0;

    /// <summary>Interruttore "Mostra": la scelta vale per tutte le rilevazioni e resta tra un avvio e l'altro.</summary>
    public bool ShowEnvironment
    {
        get => settings.ShowEnvironmentSensors;
        set
        {
            if (settings.ShowEnvironmentSensors != value)
            {
                settings.ShowEnvironmentSensors = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<AudioItem> Audio { get; } = [];

    public ObservableCollection<AnalysisItem> Analyses { get; } = [];

    public ObservableCollection<FallRiskFactorItem> FallRiskFactors { get; } = [];

    public ObservableCollection<FallRiskTestItem> FallRiskTests { get; } = [];

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

    public string TestName => TestResult?.TestName ?? string.Empty;

    public string TestValue => TestResult?.Format(CultureInfo.CurrentCulture) ?? string.Empty;

    public string TestInterpretation => TestResult?.Interpretation ?? string.Empty;

    public bool TestFlag => TestResult?.Flag == true;

    public bool TestIsOk => TestResult is { Flag: false };

    public bool TestEstimated => TestResult?.Estimated == true;

    public string TestReference => ClinicalTests.Find(TestResult?.TestId)?.Reference ?? string.Empty;

    public string TestSource => ClinicalTests.Find(TestResult?.TestId)?.Source ?? string.Empty;

    /// <summary>
    /// Scheda della stima sperimentale del rischio di caduta: solo per i test che alimentano il modello e solo
    /// se l'operatore l'ha accesa nelle Impostazioni.
    /// </summary>
    [ObservableProperty]
    public partial bool FallRiskVisible { get; set; }

    /// <summary>Probabilità 0–1 per l'indicatore; NaN se i test del soggetto non bastano.</summary>
    [ObservableProperty]
    public partial double FallRiskProbability { get; set; } = double.NaN;

    [ObservableProperty]
    public partial bool FallRiskHasEstimate { get; set; }

    /// <summary>Fascia della stima ("Fascia bassa", …).</summary>
    [ObservableProperty]
    public partial string FallRiskBand { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Color FallRiskBandColor { get; set; } = Colors.Gray;

    /// <summary>Confronto con il soggetto tipico dello studio, oppure cosa manca per avere una stima.</summary>
    [ObservableProperty]
    public partial string FallRiskDetail { get; set; } = string.Empty;

    public string FallRiskNote => Loc.F(
        "Probabilità di almeno una caduta nell'ultimo anno secondo un modello addestrato su 163 anziani fragili ({0}; AUC {1:F2}, IC 95% {2:F2}–{3:F2}). Sperimentale: non è validato sui nostri pazienti e non va usato per decisioni cliniche.",
        FallRiskModel.Default.Dataset, FallRiskModel.Default.Auc, FallRiskModel.Default.AucLow, FallRiskModel.Default.AucHigh);

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
            var mode = ClinicalPromptBuilder.Describe(_session.Mode);
            ModeText = mode.Length > 0 ? char.ToUpper(mode[0], c) + mode[1..] : mode;
            WhenText = _session.StartedAt.ToLocalTime().ToString("f", c);
            DurationText = _session.Duration is { } d ? d.ToString(@"mm\:ss", c) : "incompleta";
            OperatorName = _session.OperatorName;
            HasSummary = _session.Summary is not null;
            TestResult = _session.Summary?.Test;
            Notes = _session.Notes;

            Summary.Clear();
            Environment.Clear();
            if (_session.Summary is { } summary)
            {
                // Prima degli indicatori: quanto ci si può fidare della misura e del documento che parte per il server.
                Summary.Add(new SummaryGroup(MeasurementReliability.Section, MeasurementReliability.Assess(summary, c).Factors));
                var payload = await sync.InspectPayloadAsync(_session);
                Summary.Add(new SummaryGroup(ClinicalSensorFusion.Core.Sync.PayloadReport.Section, payload.ToRows(c)));

                var environmentSection = Loc.T(SummaryReport.SensorsSection);
                foreach (var group in SummaryReport.Build(summary, c).Where(r => r.Section != environmentSection).GroupBy(r => r.Section))
                {
                    Summary.Add(new SummaryGroup(group.Key, [.. group]));
                }

                foreach (var card in SummaryReport.Environment(summary, c))
                {
                    Environment.Add(card);
                }
            }

            OnPropertyChanged(nameof(HasEnvironment));

            Audio.Clear();
            foreach (var a in await repository.ListAudioRecordingsAsync(id))
            {
                var details = a.Features is { } f
                    ? string.Format(c, Loc.T("{0:F1} s · RMS {1:F1} dBFS · attivo {2:P0}"), a.DurationSeconds, f.RmsDbfs, f.ActiveRatio)
                    : string.Format(c, "{0:F1} s", a.DurationSeconds);
                Audio.Add(new AudioItem(a.Id, "audio.caf (48 kHz, Float32)", details, AppPaths.Audio(a)));
            }

            await LoadFallRiskAsync(c);
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

    /// <summary>La stima usa tutti i test del soggetto, non solo questo: per ciascuno vale il più recente.</summary>
    private async Task LoadFallRiskAsync(CultureInfo c)
    {
        FallRiskVisible = settings.ShowFallRiskEstimate && _session?.Summary?.Test is { } test && FallRisk.Tests.Any(t => t.Id == test.TestId);
        if (!FallRiskVisible)
        {
            return;
        }

        var code = _session!.SubjectCode;
        var sessions = (await repository.ListSessionsAsync(code, limit: 500)).Where(s => s.SubjectCode == code).ToList();
        var estimate = FallRisk.Estimate(sessions);

        FallRiskTests.Clear();
        foreach (var modelTest in FallRisk.Tests)
        {
            var latest = sessions
                .Where(s => s.Summary?.Test is { Value: { } v } t && t.TestId == modelTest.Id && double.IsFinite(v))
                .MaxBy(s => s.StartedAt);
            FallRiskTests.Add(latest?.Summary?.Test is { Value: { } value }
                ? new FallRiskTestItem(modelTest.ShortName, FallRiskTestValue(modelTest, value, c), true)
                : new FallRiskTestItem(modelTest.ShortName, Loc.T("da fare"), false));
        }

        FallRiskFactors.Clear();
        FallRiskHasEstimate = estimate is not null;
        if (estimate is null)
        {
            FallRiskProbability = double.NaN;
            FallRiskBand = Loc.T("Stima non disponibile");
            FallRiskBandColor = Color.FromArgb("#94A3B8");
            FallRiskDetail = Loc.F("Servono almeno {0} test tra TUG, 10MWT, 5TSTS e tandem, di cui uno tra TUG e 10MWT.", FallRisk.MinTests);
            return;
        }

        FallRiskProbability = estimate.Probability;
        (FallRiskBand, FallRiskBandColor) = estimate.Band switch
        {
            Core.Domain.FallRiskBand.Low => (Loc.T("Fascia bassa"), Color.FromArgb("#059669")),
            Core.Domain.FallRiskBand.Intermediate => (Loc.T("Fascia intermedia"), Color.FromArgb("#D97706")),
            _ => (Loc.T("Fascia alta"), Color.FromArgb("#DC2626")),
        };

        var typical = FallRiskModel.Default.Probability(new Dictionary<string, double>());
        FallRiskDetail = Loc.F("Un soggetto tipico dello studio è al {0}.", typical.ToString("P0", c));

        var factors = estimate.Factors();
        var largest = factors.Count == 0 ? 1 : Math.Max(factors.Max(f => Math.Abs(f.LogOdds)), 1e-9);
        foreach (var f in factors.Take(4))
        {
            // Sotto 0,05 log-odds (circa un punto percentuale) la misura è in pratica nella norma dello studio.
            var (effect, color) = f.LogOdds switch
            {
                > 0.05 => (Loc.T("alza il rischio"), Color.FromArgb("#DC2626")),
                < -0.05 => (Loc.T("abbassa il rischio"), Color.FromArgb("#059669")),
                _ => (Loc.T("nella norma"), Color.FromArgb("#94A3B8")),
            };
            FallRiskFactors.Add(new FallRiskFactorItem(FactorName(f.Feature), FactorValue(f, c), effect, color, 6 + (90 * Math.Abs(f.LogOdds) / largest)));
        }
    }

    private static string FallRiskTestValue(ClinicalTest test, double value, CultureInfo c) =>
        test.Id == ClinicalTests.TenMeterWalk.Id
            ? value.ToString("0.00", c) + " m/s"
            : value.ToString("0.0", c) + " s";

    private static string FactorName(string feature) => feature switch
    {
        "tug_time_s" => Loc.T("Timed Up and Go"),
        "gait_speed_m_s" => Loc.T("Velocità del cammino"),
        "sppb_chair_index" => Loc.T("Alzate dalla sedia (SPPB)"),
        "sppb_balance_index" => Loc.T("Equilibrio (SPPB)"),
        "cadence_strides_min" => Loc.T("Cadenza"),
        "stride_time_s" => Loc.T("Durata della falcata"),
        "stride_time_cv" => Loc.T("Variabilità del passo"),
        _ => feature,
    };

    private static string FactorValue(FallRiskFactor f, CultureInfo c) => f.Feature switch
    {
        "tug_time_s" => f.Value.ToString("0.0", c) + " s",
        "gait_speed_m_s" => f.Value.ToString("0.00", c) + " m/s",
        "sppb_chair_index" or "sppb_balance_index" => f.Value.ToString("0", c) + "/4",
        "cadence_strides_min" => Loc.F("{0} falcate/min", f.Value.ToString("0", c)),
        "stride_time_s" => f.Value.ToString("0.00", c) + " s",
        "stride_time_cv" => f.Value.ToString("P0", c),
        _ => f.Value.ToString("G3", c),
    };

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
            SyncText = Loc.F("Inviata il {0}. I grafici sono nella dashboard.", at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
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

        var messages = ClinicalPromptBuilder.Build(_session, settings.OllamaLanguage, Question, settings.OllamaPrompt);
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
            AnalysisText = text.Append("\n\n[interrotta]").ToString();
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException or OperationCanceledException)
        {
            // Timeout, rete caduta o stream troncato: il testo parziale resta visibile ma non viene salvato.
            if (text.Length > 0)
            {
                AnalysisText = text.Append(Loc.T("\n\n[incompleta: non salvata]")).ToString();
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
