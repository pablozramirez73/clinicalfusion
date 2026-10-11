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
    IDialogService dialogs) : ObservableObject, IQueryAttributable
{
    private ClinicalSession? _session;
    private CancellationTokenSource? _analysisCts;

    public ObservableCollection<SummaryGroup> Summary { get; } = [];

    public ObservableCollection<AudioItem> Audio { get; } = [];

    public ObservableCollection<AnalysisItem> Analyses { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

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

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("SessionId", out var id) && id is string sessionId)
        {
            _ = LoadAsync(sessionId);
        }
    }

    private async Task LoadAsync(string id)
    {
        try
        {
            _session = await repository.GetSessionAsync(id);
            if (_session is null)
            {
                Title = "Rilevazione non trovata";
                return;
            }

            var c = CultureInfo.CurrentCulture;
            Title = _session.SubjectCode;
            Subtitle = $"{_session.StartedAt.ToLocalTime().ToString("f", c)} · {ClinicalPromptBuilder.Describe(_session.Mode)} · {_session.OperatorName}";
            Notes = _session.Notes;

            Summary.Clear();
            if (_session.Summary is { } summary)
            {
                foreach (var group in SummaryReport.Build(summary, c).GroupBy(r => r.Section))
                {
                    Summary.Add(new SummaryGroup(group.Key, [.. group]));
                }
            }

            Audio.Clear();
            foreach (var a in await repository.ListAudioRecordingsAsync(id))
            {
                var details = a.Features is { } f
                    ? string.Format(c, "{0:F1} s · RMS {1:F1} dBFS · attivo {2:P0}", a.DurationSeconds, f.RmsDbfs, f.ActiveRatio)
                    : string.Format(c, "{0:F1} s", a.DurationSeconds);
                Audio.Add(new AudioItem(a.Id, "audio.caf (48 kHz, Float32)", details, AppPaths.Audio(a)));
            }

            await LoadAnalysesAsync();
        }
        catch (Exception ex)
        {
            await dialogs.AlertAsync("Errore", ex.Message);
        }
    }

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
            await dialogs.AlertAsync("Analisi", "La rilevazione non ha un riepilogo (forse è stata interrotta).");
            return;
        }

        if (!OllamaEndpoint.TryParse(settings.OllamaServer, out var server) || server is null)
        {
            await dialogs.AlertAsync("Analisi", "Configurare l'indirizzo del server Ollama nelle Impostazioni.");
            return;
        }

        var messages = ClinicalPromptBuilder.Build(_session, settings.OllamaLanguage, Question);
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
        catch (OperationCanceledException)
        {
            AnalysisText = text.Append("\n\n[interrotta]").ToString();
        }
        catch (Exception ex) when (ex is OllamaException or HttpRequestException)
        {
            await dialogs.AlertAsync("Ollama non raggiungibile", ex.Message);
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
            await dialogs.AlertAsync("Riproduzione", ex.Message);
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

        if (!await dialogs.ConfirmAsync("Condividi riepilogo", "Verrà condiviso solo il riepilogo numerico, senza codice soggetto né operatore. Procedere?", "Condividi"))
        {
            return;
        }

        var json = JsonSerializer.Serialize(_session.Summary, CsfJsonContext.Indented.SessionSummary);
        await Share.Default.RequestAsync(new ShareTextRequest { Title = "Riepilogo rilevazione", Text = json });
    }
}
