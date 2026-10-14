using ClinicalSensorFusion.App.ViewModels;

namespace ClinicalSensorFusion.App.Views;

public partial class BluetoothSensorsPage : ContentPage
{
    private readonly BluetoothSensorsViewModel _viewModel;

    public BluetoothSensorsPage(BluetoothSensorsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.Appearing();
    }

    protected override void OnDisappearing()
    {
        _viewModel.Disappearing();
        base.OnDisappearing();
    }
}
