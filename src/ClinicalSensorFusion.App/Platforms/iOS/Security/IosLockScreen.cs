using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.App.Services;
using UIKit;

namespace ClinicalSensorFusion.App.Platforms.iOS.Security;

/// <summary>
/// Copertura del blocco in una UIWindow propria, sopra quella dell'app: è il modo usuale su iOS per una
/// schermata di privacy, perché resta in cima a qualunque cosa l'app stia mostrando (avvisi, fogli modali).
/// </summary>
internal sealed class IosLockScreen : ILockScreen
{
    private static readonly UIColor Background = UIColor.FromRGB(0x0B, 0x3D, 0x5C);
    private static readonly UIColor Accent = UIColor.FromRGB(0x4F, 0xD1, 0xC5);
    private static readonly UIColor ButtonText = UIColor.FromRGB(0x06, 0x2B, 0x42);

    private UIWindow? _window;
    private UILabel? _message;
    private Action? _unlockRequested;

    public void Show(string unlockTitle, Action unlockRequested) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _unlockRequested = unlockRequested;
        if (_window is not null)
        {
            return;
        }

        var scene = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().FirstOrDefault();
        if (scene is null)
        {
            return;
        }

        var title = new UILabel
        {
            Text = "SensorFusion Clinic",
            TextColor = UIColor.White,
            Font = UIFont.BoldSystemFontOfSize(28)!,
            TextAlignment = UITextAlignment.Center,
        };
        _message = new UILabel
        {
            Text = Loc.T("I dati clinici sono protetti."),
            TextColor = UIColor.FromRGB(0xCF, 0xE3, 0xEC),
            Font = UIFont.SystemFontOfSize(15)!,
            TextAlignment = UITextAlignment.Center,
            Lines = 0,
        };

        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.Title = unlockTitle;
        configuration.BaseBackgroundColor = Accent;
        configuration.BaseForegroundColor = ButtonText;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Large;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(14, 22, 14, 22);
        var button = new UIButton(UIButtonType.System) { Configuration = configuration };
        button.TouchUpInside += (_, _) => _unlockRequested?.Invoke();

        var stack = new UIStackView([title, _message, button])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        stack.SetCustomSpacing(26, _message);

        var controller = new UIViewController();
        controller.View!.BackgroundColor = Background;
        controller.View.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            stack.CenterYAnchor.ConstraintEqualTo(controller.View.CenterYAnchor),
            stack.LeadingAnchor.ConstraintEqualTo(controller.View.LeadingAnchor, 32),
            stack.TrailingAnchor.ConstraintEqualTo(controller.View.TrailingAnchor, -32),
        ]);

        _window = new UIWindow(scene)
        {
            RootViewController = controller,
            // Sopra la finestra dell'app e i suoi avvisi; la richiesta di Face ID è del sistema e resta più in alto.
            WindowLevel = UIWindowLevel.Alert + 1,
            Hidden = false,
        };
    });

    public void SetMessage(string message) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_message is not null)
        {
            _message.Text = message;
        }
    });

    public void Hide() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_window is null)
        {
            return;
        }

        _window.Hidden = true;
        _window.Dispose();
        _window = null;
        _message = null;
    });
}
