using ClinicalSensorFusion.App.ViewModels;

namespace ClinicalSensorFusion.App.Views;

public partial class RemoteAccessPage : ContentPage
{
    public RemoteAccessPage(RemoteAccessViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
