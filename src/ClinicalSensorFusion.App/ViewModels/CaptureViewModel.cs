using ClinicalSensorFusion.Core.Localization;
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
    private TestRun? _run;
    private IDispatcherTimer? _timer;
    private bool _previewActive;

    private readonly MetricTile _elapsed = new(Loc.T("Tempo"));
    private readonly MetricTile _knee = new(Loc.T("Ginocchio dx"));
    private readonly MetricTile _elbow = new(Loc.T("Gomito dx"));
    private readonly MetricTile _distance = new(Loc.T("Distanza"));
    private readonly MetricTile _audio = new(Loc.T("Audio"));
    private readonly MetricTile _steps = new(Loc.T("Passi"));
    private readonly MetricTile _altitude = new(Loc.T("Quota rel."));
    private readonly MetricTile _light = new(Loc.T("Luce"));
    private readonly MetricTile _thermal = new(Loc.T("Temperatura"));

    public CaptureViewModel(ICaptureEngine engine, CaptureCoordinator coordinator, MauiAppSettings settings, IDialogService dialogs, ISpeechGuide speech)
    {
        _speech = speech;
        _engine = engine;
        _coordinator = coordinator;
        _settings = settings;
        _dialogs = dialogs;

        Modes =
        [
            new(CaptureMode.BodyLidar, Loc.T("Corpo"), engine.IsLidarSupported
                ? Loc.T("Postura, escursione articolare e cammino (MediaPipe Pose, LiDAR a richiesta)")
                : Loc.T("Postura, escursione articolare e cammino (MediaPipe Pose)")),
            new(CaptureMode.HandsLidar, Loc.T("Mani"), Loc.T("Motricità fine e tremore (MediaPipe Hands)")),
            new(CaptureMode.FaceTrueDepth, Loc.T("Volto"), DeviceInfo.Current.Platform == DevicePlatform.iOS
                ? Loc.T("Mimica facciale (TrueDepth + MediaPipe Face)")
                : Loc.T("Mimica facciale (fotocamera frontale + MediaPipe Face)")),
        ];
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

    /// <summary>
    /// Test standardizzati della modalità scelta (corpo: CDC STEADI e core set ANPT/APTA; mani: finger
    /// tapping MDS-UPDRS), preceduti dalla rilevazione libera.
    /// </summary>
    public IReadOnlyList<TestOption> Tests { get; private set; } = [new TestOption(null, Loc.T("Rilevazione libera"))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Test), nameof(HasTest), nameof(TestSteps), nameof(RecordHint), nameof(SetupSummary))]
    public partial TestOption SelectedTest { get; set; }

    /// <summary>
    /// Test e codice soggetto in una riga, sempre visibile sul pannello abbassato: dice che cosa si sta per
    /// registrare e fa da ingresso ai parametri (toccandola il pannello si alza).
    /// </summary>
    public string SetupSummary =>
        $"{SelectedTest?.Title ?? Loc.T("Rilevazione libera")} · {(string.IsNullOrWhiteSpace(SubjectCode) ? Loc.T("codice soggetto da inserire") : SubjectCode.Trim())}";

    public ClinicalTest? Test => ShowTests ? SelectedTest?.Test : null;

    // Quando l'elenco cambia il selettore azzera la scelta: si torna alla rilevazione libera.
    partial void OnSelectedTestChanged(TestOption value)
    {
        if (value is null)
        {
            SelectedTest = Tests[0];
        }
    }

    public bool HasTest => Test is not null;

    public bool ShowTests => Tests.Count > 1;

    public bool CanChooseTest => !IsRecording;

    /// <summary>Passi del protocollo, numerati, per la scheda delle istruzioni.</summary>
    public string TestSteps => Test is { } test
        ? string.Join("\n", test.Steps.Select((step, i) => $"{i + 1}. {step}"))
        : string.Empty;

    public ObservableCollection<MetricTile> Tiles { get; }

    public ObservableCollection<SensorStatus> Sensors { get; } = [];

    public bool LidarAvailable => _engine.IsLidarSupported;

    /// <summary>L'interruttore del LiDAR ha senso solo in modalità corpo, su un telefono che lo monta.</summary>
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
    [NotifyPropertyChangedFor(nameof(SetupSummary))]
    public partial string SubjectCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectModeCommand))]
    [NotifyPropertyChangedFor(nameof(CanChangeLidar), nameof(CanChooseTest), nameof(RecordHint))]
    public partial bool IsRecording { get; set; }

    /// <summary>La voce sta spiegando l'esame o contando alla rovescia: si registra già, il tempo del test parte al «Via».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordHint))]
    public partial bool IsGuiding { get; set; }

    /// <summary>Conto alla rovescia mostrato sul pulsante durante la guida ("3", "2", "1").</summary>
    [ObservableProperty]
    public partial string GuideCountdown { get; set; } = string.Empty;

    /// <summary>Che cosa fa il pulsante rosso in questo momento.</summary>
    public string RecordHint => (IsRecording, IsGuiding, HasTest) switch
    {
        (true, true, _) => Loc.T("La voce sta spiegando l'esame · tocca per terminare"),
        (true, _, _) => Loc.T("Tocca per terminare"),
        (_, _, true) => Loc.T("Avvia: la voce guida il paziente"),
        _ => Loc.T("Avvia la rilevazione"),
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

    /// <summary>Serve un dato che sta nel pannello dei parametri (per esempio il codice soggetto): va mostrato.</summary>
    public event EventHandler? DetailsNeeded;

    public async Task OnAppearingAsync(IDispatcher dispatcher)
    {
        _timer ??= CreateTimer(dispatcher);
        _timer.Start();

        if (!_settings.IsOperatorConfiguredSafe())
        {
            Warning = Loc.T("Configurare l'operatore nelle Impostazioni prima di acquisire.");
        }
        else
        {
            Warning = null;
        }

        if (!await _engine.RequestPermissionsAsync(_settings.RecordLocation))
        {
            Warning = Loc.T("Servono i permessi per fotocamera e microfono (Impostazioni del telefono → SensorFusion Clinic).");
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
        // Ogni modalità ha i suoi test: l'elenco cambia e si riparte dalla rilevazione libera.
        Tests = [new TestOption(null, Loc.T("Rilevazione libera")), .. ClinicalTests.For(value.Mode).Select(t => new TestOption(t, t.Name))];
        OnPropertyChanged(nameof(Tests));
        SelectedTest = Tests[0];
        OnPropertyChanged(nameof(ShowTests));

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

    private bool CanStart() => !IsRecording && !IsBusy && !string.IsNullOrWhiteSpace(SubjectCode);

    /// <summary>
    /// Pulsante rosso sotto l'anteprima: avvia la rilevazione (e con essa la guida vocale) oppure la termina.
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

        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SubjectCode))
        {
            DetailsNeeded?.Invoke(this, EventArgs.Empty);
            await _dialogs.AlertAsync(Loc.T("Codice soggetto"), Loc.T("Inserisci il codice soggetto prima di avviare."));
            return;
        }

        await StartAsync();
    }

    /// <summary>
    /// Voce che dirige il paziente, a registrazione già avviata: spiega l'esame, conta alla rovescia e dà il
    /// «Via», da cui parte il tempo del test. Parte dopo l'avvio perché solo allora l'audio dell'app esce
    /// dall'altoparlante anche con il telefono in silenzioso.
    /// </summary>
    private async Task RunGuideAsync(ClinicalTest? test, CaptureMode mode)
    {
        using var cts = new CancellationTokenSource();
        _guideCts = cts;
        _run = test is null ? null : new TestRun(test);
        IsGuiding = true;
        try
        {
            if (test is null)
            {
                await _speech.SpeakAsync(FreeCaptureGuide(mode), cts.Token);
                return;
            }

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
            // Rilevazione chiusa mentre la voce parlava.
            return;
        }
        catch (Exception ex)
        {
            // La voce è un aiuto: se la sintesi non è disponibile il test si può comunque eseguire.
            Warning = Loc.F("Guida vocale non disponibile: {0}", ex.Message);
        }
        finally
        {
            GuideCountdown = string.Empty;
            IsGuiding = false;
            if (ReferenceEquals(_guideCts, cts))
            {
                _guideCts = null;
            }
        }

        if (test is not null && IsRecording)
        {
            _coordinator.MarkTestStart();
        }
    }

    private static string FreeCaptureGuide(CaptureMode mode) => mode switch
    {
        CaptureMode.HandsLidar => Loc.T("Inizio della rilevazione delle mani. Tenga le mani ben visibili davanti alla fotocamera e segua le indicazioni dell'operatore."),
        CaptureMode.FaceTrueDepth => Loc.T("Inizio della rilevazione del volto. Guardi verso lo schermo, tenga il viso ben illuminato e segua le indicazioni dell'operatore."),
        _ => Loc.T("Inizio della rilevazione del movimento. Resti a figura intera nell'inquadratura e segua le indicazioni dell'operatore."),
    };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            await _coordinator.StartAsync(SubjectCode, SelectedMode.Mode, Notes, _settings.RecordLocation, Test);
            IsRecording = true;
            RefreshSensors();

            // Non si aspetta la fine della voce: l'operatore deve poter terminare in qualunque momento.
            _ = RunGuideAsync(Test, SelectedMode.Mode);
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync(Loc.T("Impossibile avviare"), ex.Message);
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
        _guideCts?.Cancel();
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
            await _dialogs.AlertAsync(Loc.T("Errore in chiusura"), ex.Message);
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
                Warning = Loc.T("Questo telefono non ha la fotocamera necessaria al tracciamento del volto.");
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
        // Con un test il tempo che conta è quello dal «Via», non dall'inizio della registrazione.
        var run = IsRecording ? _run : null;
        var seconds = run is null ? _coordinator.Elapsed : _coordinator.TestElapsed;
        var time = seconds is { } value ? TimeSpan.FromSeconds(value).ToString(@"mm\:ss", c) : "—";
        _elapsed.Value = IsRecording ? time : "—";
        Elapsed = !IsRecording ? "00:00" : seconds is null ? Loc.T("guida") : time;

        if (run is not null && seconds is not null)
        {
            // Le regole del protocollo (frasi previste, chiusura automatica) sono in TestRun: qui si eseguono.
            var step = run.Advance(seconds, live);
            if (step.Cue is { } cue)
            {
                _ = SpeakQuietlyAsync(cue);
            }

            if (run.Done is { } done)
            {
                Elapsed = $"{time} · {done}/{run.Required}";
            }

            if (run.Test.AutoStopSeconds is { } limit)
            {
                Elapsed = $"{time} / {TimeSpan.FromSeconds(limit).ToString(@"mm\:ss", c)}";
            }

            if (step.CompletedAt is { } at)
            {
                _coordinator.MarkTestEnd(at);
            }

            if (step.Finished)
            {
                // Avvisa il paziente: l'operatore può essere lontano dal telefono.
                _ = SpeakQuietlyAsync(Loc.T("Stop. Il test è finito."));
            }

            // Si riprova al passo successivo se in questo momento la chiusura non è possibile.
            if (run.State == TestRunState.Finished && StopCommand.CanExecute(null))
            {
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
            2 => Loc.T("elevata ⚠"),
            _ => Loc.T("critica ⚠"),
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
