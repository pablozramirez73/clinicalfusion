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

    public CaptureViewModel(ICaptureEngine engine, CaptureCoordinator coordinator, MauiAppSettings settings, IDialogService dialogs)
    {
        _engine = engine;
        _coordinator = coordinator;
        _settings = settings;
        _dialogs = dialogs;

        Modes =
        [
            new(CaptureMode.BodyLidar, "Corpo", "Postura, escursione articolare e cammino (LiDAR + MediaPipe Pose)"),
            new(CaptureMode.HandsLidar, "Mani", "Motricità fine e tremore (LiDAR + MediaPipe Hands)"),
            new(CaptureMode.FaceTrueDepth, "Volto", "Mimica facciale (TrueDepth + MediaPipe Face)"),
        ];
        SelectedMode = Modes[0];
        DepthOpacity = settings.ShowDepthOverlay ? 0.35 : 0;
        Tiles = [_elapsed, _knee, _elbow, _distance, _audio, _steps, _altitude, _light, _thermal];

        _engine.OverlayUpdated += OnOverlay;
    }

    /// <summary>Richiesta di ridisegno dell'overlay (gestita dalla pagina sul main thread).</summary>
    public event EventHandler? OverlayInvalidated;

    public LandmarkOverlayDrawable Overlay { get; } = new();

    public IReadOnlyList<CaptureModeOption> Modes { get; }

    public ObservableCollection<MetricTile> Tiles { get; }

    public ObservableCollection<SensorStatus> Sensors { get; } = [];

    public bool LidarAvailable => _engine.IsLidarSupported;

    [ObservableProperty]
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
    public partial bool IsRecording { get; set; }

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
        else if (!_engine.IsLidarSupported)
        {
            Warning = "LiDAR non presente: gli angoli useranno la stima 3D di MediaPipe (meno accurata).";
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

    partial void OnDepthOpacityChanged(double value) => _settings.ShowDepthOverlay = value > 0;

    private bool CanStart() => !IsRecording && !IsBusy && !string.IsNullOrWhiteSpace(SubjectCode);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            await _coordinator.StartAsync(SubjectCode, SelectedMode.Mode, Notes, _settings.RecordLocation);
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
