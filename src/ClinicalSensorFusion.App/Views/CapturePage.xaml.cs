using ClinicalSensorFusion.App.ViewModels;

namespace ClinicalSensorFusion.App.Views;

public partial class CapturePage : ContentPage
{
    private readonly CaptureViewModel _viewModel;

    public CapturePage(CaptureViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.OverlayInvalidated += (_, _) => OverlayView.Invalidate();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Lo schermo non deve spegnersi durante un'acquisizione.
        DeviceDisplay.Current.KeepScreenOn = true;
        await _viewModel.OnAppearingAsync(Dispatcher);
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_viewModel.IsRecording)
        {
            DeviceDisplay.Current.KeepScreenOn = false;
        }

        await _viewModel.OnDisappearingAsync();
    }
}
