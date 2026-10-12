using System.Collections.ObjectModel;
using System.Globalization;
using ClinicalSensorFusion.App.Controls;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

public sealed partial class CaptureModeOption(CaptureMode mode, string title, string description) : ObservableObject
{
    public CaptureMode Mode { get; } = mode;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Voce del selettore dei test; <see cref="Test"/> null = rilevazione libera.</summary>
public sealed record TestOption(ClinicalTest? Test, string Title);

public sealed partial class MetricTile(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Value { get; set; } = "—";
}

/// <summary>
/// Schermata di acquisizione: anteprima con overlay, scelta della modalità, avvio/arresto e valori live.
/// </summary>
public sealed partial class CaptureViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureEngine _engine;
    private readonly CaptureCoordinator _coordinator;
    private readonly MauiAppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ISpeechGuide _speech;
    private CancellationTokenSource? _guideCts;
    private IDispatcherTimer? _timer;
    private bool _previewActive;

    private readonly MetricTile _elapsed = new("Tempo");
    private readonly MetricTile _knee = new("Ginocchio dx");
    private readonly MetricTile _elbow = new("Gomito dx");
    private readonly MetricTile _distance = new("Distanza");
    private readonly MetricTile _audio = new("Audio");
    private readonly MetricTile _steps = new("Passi");
    private readonly MetricTile _altitude = new("Quota rel.");
    private readonly MetricTile _light = new("Luce");
    private readonly MetricTile _thermal = new("Temperatura");

    public CaptureViewModel(ICaptureEngine engine, CaptureCoordinator coordinator, MauiAppSettings settings, IDialogService dialogs, ISpeechGuide speech)
    {
        _speech = speech;
        _engine = engine;
        _coordinator = coordinator;
        _settings = settings;
        _dialogs = dialogs;

        Modes =
        [
            new(CaptureMode.BodyLidar, "Corpo", "Postura, escursione articolare e cammino (MediaPipe Pose, LiDAR a richiesta)"),
            new(CaptureMode.HandsLidar, "Mani", "Motricità fine e tremore (MediaPipe Hands)"),
            new(CaptureMode.FaceTrueDepth, "Volto", "Mimica facciale (TrueDepth + MediaPipe Face)"),
        ];
        Tests = [new TestOption(null, "Rilevazione libera"), .. ClinicalTests.All.Select(t => new TestOption(t, t.Name))];
        SelectedTest = Tests[0];
        SelectedMode = Modes[0];
        UseLidar = settings.UseLidar;
        DepthOpacity = settings.ShowDepthOverlay ? 0.35 : 0;
        Tiles = [_elapsed, _knee, _elbow, _distance, _audio, _steps, _altitude, _light, _thermal];

        _engine.OverlayUpdated += OnOverlay;
    }

    /// <summary>Richiesta di ridisegno dell'overlay (gestita dalla pagina sul main thread).</summary>
    public event EventHandler? OverlayInvalidated;

    public LandmarkOverlayDrawable Overlay { get; } = new();

    public IReadOnlyList<CaptureModeOption> Modes { get; }

    /// <summary>Test funzionali standardizzati (CDC STEADI, core set ANPT/APTA), tutti in modalità corpo.</summary>
    public IReadOnlyList<TestOption> Tests { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Test), nameof(HasTest), nameof(TestSteps), nameof(RecordHint))]
    public partial TestOption SelectedTest { get; set; }

    public ClinicalTest? Test => ShowTests ? SelectedTest.Test : null;

    public bool HasTest => Test is not null;

    public bool ShowTests => SelectedMode.Mode == CaptureMode.BodyLidar;

    public bool CanChooseTest => !IsRecording;

    /// <summary>Passi del protocollo, numerati, per la scheda delle istruzioni.</summary>
    public string TestSteps => Test is { } test
        ? string.Join("\n", test.Steps.Select((step, i) => $"{i + 1}. {step}"))
        : string.Empty;

    public ObservableCollection<MetricTile> Tiles { get; }

    public ObservableCollection<SensorStatus> Sensors { get; } = [];

    public bool LidarAvailable => _engine.IsLidarSupported;

    /// <summary>L'interruttore del LiDAR ha senso solo in modalità corpo, su un iPhone che lo monta.</summary>
    public bool ShowLidarOption => LidarAvailable && SelectedMode.Mode == CaptureMode.BodyLidar;

    /// <summary>La mappa di profondità si può sovrapporre solo se il LiDAR è acceso.</summary>
    public bool ShowDepthToggle => ShowLidarOption && UseLidar;

    public bool CanChangeLidar => !IsRecording;

    /// <summary>Profondità LiDAR: spenta di norma, l'operatore la accende quando gli servono misure in metri.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDepthToggle))]
    public partial bool UseLidar { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLidarOption), nameof(ShowDepthToggle), nameof(ShowTests), nameof(Test), nameof(HasTest), nameof(TestSteps), nameof(RecordHint))]
    public partial CaptureModeOption SelectedMode { get; set; }

    /// <summary>Tempo trascorso, mostrato accanto all'indicatore REC sull'anteprima.</summary>
    [ObservableProperty]
    public partial string Elapsed { get; set; } = "00:00";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string SubjectCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectModeCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeLidar), nameof(CanChooseTest), nameof(RecordHint))]
    public partial bool IsRecording { get; set; }

    /// <summary>La guida vocale sta parlando o contando alla rovescia: la registrazione non è ancora partita.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordHint))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsGuiding { get; set; }

    /// <summary>Conto alla rovescia mostrato sul pulsante durante la guida ("3", "2", "1").</summary>
    [ObservableProperty]
    public partial string GuideCountdown { get; set; } = string.Empty;

    /// <summary>Che cosa fa il pulsante rosso in questo momento.</summary>
    public string RecordHint => (IsRecording, IsGuiding, HasTest) switch
    {
        (true, _, _) => "Tocca per terminare",
        (_, true, _) => "Guida vocale in corso · tocca per annullare",
        (_, _, true) => "Avvia con guida vocale",
        _ => "Avvia la rilevazione",
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Caption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double DepthOpacity { get; set; }

    [ObservableProperty]
    public partial string? Warning { get; set; }

    public async Task OnAppearingAsync(IDispatcher dispatcher)
    {
        _timer ??= CreateTimer(dispatcher);
        _timer.Start();

        if (!_settings.IsOperatorConfiguredSafe())
        {
            Warning = "Configurare l'operatore nelle Impostazioni prima di acquisire.";
        }
        else
        {
            Warning = null;
        }

        if (!await _engine.RequestPermissionsAsync(_settings.RecordLocation))
        {
            Warning = "Servono i permessi per fotocamera e microfono (Impostazioni iOS → SensorFusion Clinic).";
            return;
        }

        await StartPreviewAsync();
    }

    public async Task OnDisappearingAsync()
    {
        // Durante una registrazione si lascia tutto acceso: l'operatore può cambiare scheda per errore.
        if (IsRecording)
        {
            return;
        }

        _timer?.Stop();
        await _engine.StopPreviewAsync();
        _previewActive = false;
        Overlay.Clear();
    }

    public void Dispose()
    {
        _engine.OverlayUpdated -= OnOverlay;
        _guideCts?.Cancel();
        _timer?.Stop();
    }

    async partial void OnSelectedModeChanged(CaptureModeOption value)
    {
        foreach (var option in Modes)
        {
            option.IsSelected = ReferenceEquals(option, value);
        }

        if (_previewActive && !IsRecording)
        {
            await StartPreviewAsync();
        }
    }

    async partial void OnUseLidarChanged(bool value)
    {
        _settings.UseLidar = value;
        _engine.UseLidar = value;

        // La profondità si attiva con la configurazione della sessione: serve riavviare l'anteprima.
        if (_previewActive && !IsRecording)
        {
            await StartPreviewAsync();
        }
    }

    partial void OnDepthOpacityChanged(double value) => _settings.ShowDepthOverlay = value > 0;

    private bool CanStart() => !IsRecording && !IsBusy && !IsGuiding && !string.IsNullOrWhiteSpace(SubjectCode);

    /// <summary>
    /// Pulsante rosso sotto l'anteprima. Con un test selezionato legge la guida al paziente, conta alla
    /// rovescia e avvia la registrazione sul «Via»; senza test avvia subito. Durante la guida la annulla,
    /// durante la registrazione la termina.
    /// </summary>
    [RelayCommand]
    private async Task RecordButtonAsync()
    {
        if (IsRecording)
        {
            if (StopCommand.CanExecute(null))
            {
                await StopCommand.ExecuteAsync(null);
            }

            return;
        }

        if (IsGuiding)
        {
            _guideCts?.Cancel();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SubjectCode))
        {
            await _dialogs.AlertAsync("Codice soggetto", "Inserisci il codice soggetto prima di avviare.");
            return;
        }

        if (Test is not { } test)
        {
            await StartAsync();
            return;
        }

        using var cts = new CancellationTokenSource();
        _guideCts = cts;
        IsGuiding = true;
        try
        {
            await _speech.SpeakAsync(test.AudioGuide, cts.Token);
            await _speech.SpeakAsync("Pronti?", cts.Token);
            foreach (var (digit, word) in new[] { ("3", "Tre"), ("2", "Due"), ("1", "Uno") })
            {
                GuideCountdown = digit;
                await _speech.SpeakAsync(word, cts.Token);
                await Task.Delay(350, cts.Token);
            }

            GuideCountdown = string.Empty;
            await _speech.SpeakAsync("Via!", cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // La voce è un aiuto: se la sintesi non è disponibile il test si può comunque eseguire.
            Warning = $"Guida vocale non disponibile: {ex.Message}";
        }
        finally
        {
            GuideCountdown = string.Empty;
            IsGuiding = false;
            _guideCts = null;
        }

        await StartAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            await _coordinator.StartAsync(SubjectCode, SelectedMode.Mode, Notes, _settings.RecordLocation, Test);
            IsRecording = true;
            RefreshSensors();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Impossibile avviare", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStop() => IsRecording && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsBusy = true;
        try
        {
            var session = await _coordinator.StopAsync();
            IsRecording = false;
            SubjectCode = string.Empty;
            Notes = null;
            RefreshSensors();
            await Shell.Current.GoToAsync(nameof(SessionDetailPage), new Dictionary<string, object>
            {
                ["SessionId"] = session.Id,
                // Il dettaglio propone (o avvia, se automatico) l'invio al server clinico.
                [SessionDetailViewModel.JustRecordedKey] = true,
            });
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Errore in chiusura", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // La modalità non si cambia a registrazione avviata: i flussi sono già aperti per quella scelta.
    private bool CanSelectMode() => !IsRecording;

    [RelayCommand(CanExecute = nameof(CanSelectMode))]
    private void SelectMode(CaptureModeOption option) => SelectedMode = option;

    [RelayCommand]
    private void ToggleDepth() => DepthOpacity = DepthOpacity > 0 ? 0 : 0.35;

    private async Task SpeakQuietlyAsync(string text)
    {
        try
        {
            await _speech.SpeakAsync(text);
        }
        catch (Exception)
        {
            // Solo un avviso sonoro: un errore della sintesi non deve disturbare la chiusura del test.
        }
    }

    private async Task StartPreviewAsync()
    {
        try
        {
            if (SelectedMode.Mode == CaptureMode.FaceTrueDepth && !_engine.IsFaceTrackingSupported)
            {
                Warning = "Questo iPhone non supporta il tracciamento del volto TrueDepth.";
                return;
            }

            await _engine.StartPreviewAsync(SelectedMode.Mode);
            _previewActive = true;
            RefreshSensors();
        }
        catch (Exception ex)
        {
            Warning = ex.Message;
        }
    }

    private void OnOverlay(object? sender, OverlayFrame frame)
    {
        Overlay.Update(frame);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Caption = string.Create(CultureInfo.CurrentCulture, $"{frame.Caption} · {frame.InferenceMs:F0} ms");
            OverlayInvalidated?.Invoke(this, EventArgs.Empty);
        });
    }

    private IDispatcherTimer CreateTimer(IDispatcher dispatcher)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => UpdateTiles(_coordinator.Live);
        return timer;
    }

    private void RefreshSensors()
    {
        Sensors.Clear();
        foreach (var s in _engine.GetSensorStatus())
        {
            Sensors.Add(s);
        }
    }

    private void UpdateTiles(LiveMetrics? live)
    {
        var c = CultureInfo.CurrentCulture;
        _elapsed.Value = IsRecording ? TimeSpan.FromSeconds(_coordinator.Elapsed).ToString(@"mm\:ss", c) : "—";
        Elapsed = IsRecording ? _elapsed.Value : "00:00";

        // I test a durata fissa (30 secondi, 6 minuti, 10 secondi) si chiudono da soli allo scadere.
        if (IsRecording && Test?.AutoStopSeconds is { } limit)
        {
            Elapsed = $"{_elapsed.Value} / {TimeSpan.FromSeconds(limit).ToString(@"mm\:ss", c)}";
            if (_coordinator.Elapsed >= limit && StopCommand.CanExecute(null))
            {
                // Avvisa il paziente: l'operatore può essere lontano dal telefono.
                _ = SpeakQuietlyAsync("Stop. Il test è finito.");
                StopCommand.Execute(null);
            }
        }
        if (live is null)
        {
            return;
        }

        _knee.Value = Angle(live.JointAngles, Joint.RightKneeFlexion);
        _elbow.Value = Angle(live.JointAngles, Joint.RightElbowFlexion);
        _distance.Value = live.SubjectDistance is { } d ? string.Format(c, "{0:F2} m", d) : "—";
        _audio.Value = live.AudioRmsDbfs is { } db ? string.Format(c, "{0:F0} dB", db) : "—";
        _steps.Value = live.Steps.ToString(c);
        _altitude.Value = live.RelativeAltitude is { } alt ? string.Format(c, "{0:+0.0;-0.0} m", alt) : "—";
        _light.Value = live.AmbientLux is { } lux ? string.Format(c, "{0:F0} lm", lux) : "—";
        _thermal.Value = live.ThermalState switch
        {
            0 => "normale",
            1 => "discreta",
            2 => "elevata ⚠",
            _ => "critica ⚠",
        };

        string Angle(float[]? angles, Joint joint) =>
            angles is not null && (int)joint < angles.Length && float.IsFinite(angles[(int)joint])
                ? string.Format(c, "{0:F0}°", angles[(int)joint])
                : "—";
    }
}

internal static class SettingsExtensions
{
    public static bool IsOperatorConfiguredSafe(this MauiAppSettings s) =>
        !string.IsNullOrWhiteSpace(s.OperatorId) && !string.IsNullOrWhiteSpace(s.OperatorName);
}
