using ClinicalSensorFusion.Core.Localization;
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
        _viewModel.DetailsNeeded += (_, _) => SetSheetExpanded(true);
        _viewModel.PropertyChanged += (_, e) =>
        {
            // In registrazione la camera deve vedersi tutta; alla fine servono di nuovo i parametri.
            if (e.PropertyName == nameof(CaptureViewModel.IsRecording))
            {
                SetSheetExpanded(!_viewModel.IsRecording);
            }
        };
    }

    private const double ExpandedFraction = 0.64;
    private const string SheetAnimation = "sheet";

    private bool _sheetExpanded;
    private double _sheetCollapsedHeight = 236;

    private void OnSheetHandleTapped(object? sender, TappedEventArgs e) => SetSheetExpanded(!_sheetExpanded);

    private void OnSheetSwipedUp(object? sender, SwipedEventArgs e) => SetSheetExpanded(true);

    private void OnSheetSwipedDown(object? sender, SwipedEventArgs e) => SetSheetExpanded(false);

    /// <summary>Il pannello abbassato è alto quanto la sua intestazione, qualunque sia la dimensione del testo.</summary>
    private void OnSheetHeaderSizeChanged(object? sender, EventArgs e)
    {
        var height = SheetHeader.Height;
        if (height <= 0 || Math.Abs(height - _sheetCollapsedHeight) < 0.5)
        {
            return;
        }

        _sheetCollapsedHeight = height;
        SheetSpace.HeightRequest = height;
        if (!_sheetExpanded)
        {
            Sheet.AbortAnimation(SheetAnimation);
            Sheet.HeightRequest = height;
        }
    }

    private void SetSheetExpanded(bool expanded)
    {
        if (_sheetExpanded == expanded)
        {
            return;
        }

        _sheetExpanded = expanded;
        SheetToggleLabel.Text = expanded ? Loc.T("Chiudi") : Loc.T("Modifica");
        var target = expanded ? Math.Max(_sheetCollapsedHeight, Height * ExpandedFraction) : _sheetCollapsedHeight;
        if (expanded)
        {
            SheetBody.IsVisible = true;
        }

        Sheet.AbortAnimation(SheetAnimation);
        new Animation(value => Sheet.HeightRequest = value, Sheet.Height > 0 ? Sheet.Height : _sheetCollapsedHeight, target, Easing.CubicOut)
            .Commit(Sheet, SheetAnimation, length: 220, finished: (_, _) =>
            {
                Sheet.HeightRequest = _sheetExpanded ? target : _sheetCollapsedHeight;
                // Abbassato, il contenuto non deve essere raggiungibile da tastiera o lettore di schermo.
                SheetBody.IsVisible = _sheetExpanded;
            });
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
