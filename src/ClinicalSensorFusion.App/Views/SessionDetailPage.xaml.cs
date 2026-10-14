using ClinicalSensorFusion.App.ViewModels;

namespace ClinicalSensorFusion.App.Views;

public partial class SessionDetailPage : ContentPage
{
    public SessionDetailPage(SessionDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
