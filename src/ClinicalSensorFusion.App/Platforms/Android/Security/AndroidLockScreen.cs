using ClinicalSensorFusion.Core.Localization;
using Android.App;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Localization;
using AColor = Android.Graphics.Color;

namespace ClinicalSensorFusion.App.Platforms.Android.Security;

/// <summary>
/// Copertura del blocco in una finestra di dialogo a tutto schermo, sopra l'attività: resta in cima a
/// qualunque cosa l'app stia mostrando (avvisi, pagine modali) e non dipende dallo stato della navigazione.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "La finestra è rilasciata in Hide; il servizio vive quanto l'app.")]
internal sealed class AndroidLockScreen : ILockScreen
{
    private static readonly AColor Background = AColor.Rgb(0x0B, 0x3D, 0x5C);
    private static readonly AColor Accent = AColor.Rgb(0x4F, 0xD1, 0xC5);
    private static readonly AColor ButtonText = AColor.Rgb(0x06, 0x2B, 0x42);

    private Dialog? _dialog;
    private TextView? _message;
    private Action? _unlockRequested;

    public void Show(string unlockTitle, Action unlockRequested) => MainThread.BeginInvokeOnMainThread(() =>
    {
        _unlockRequested = unlockRequested;
        if (_dialog is not null || Platform.CurrentActivity is not { } activity)
        {
            return;
        }

        int Dp(float value) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, activity.Resources!.DisplayMetrics);

        var title = new TextView(activity) { Text = "SensorFusion Clinic", Gravity = GravityFlags.Center };
        title.SetTextColor(AColor.White);
        title.SetTextSize(ComplexUnitType.Sp, 28);
        title.SetTypeface(null, global::Android.Graphics.TypefaceStyle.Bold);

        _message = new TextView(activity) { Text = Loc.T("I dati clinici sono protetti."), Gravity = GravityFlags.Center };
        _message.SetTextColor(AColor.Rgb(0xCF, 0xE3, 0xEC));
        _message.SetTextSize(ComplexUnitType.Sp, 15);

        var shape = new GradientDrawable();
        shape.SetColor(Accent);
        shape.SetCornerRadius(Dp(14));
        var button = new global::Android.Widget.Button(activity) { Text = unlockTitle, Background = shape };
        button.SetAllCaps(false);
        button.SetTextColor(ButtonText);
        button.SetPadding(Dp(22), Dp(14), Dp(22), Dp(14));
        button.Click += (_, _) => _unlockRequested?.Invoke();

        var layout = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        layout.SetGravity(GravityFlags.Center);
        layout.SetBackgroundColor(Background);
        layout.SetPadding(Dp(32), 0, Dp(32), 0);
        var wrap = ViewGroup.LayoutParams.WrapContent;
        layout.AddView(title, new LinearLayout.LayoutParams(wrap, wrap));
        layout.AddView(_message, new LinearLayout.LayoutParams(wrap, wrap) { TopMargin = Dp(10) });
        layout.AddView(button, new LinearLayout.LayoutParams(wrap, wrap) { TopMargin = Dp(26) });

        _dialog = new Dialog(activity, global::Android.Resource.Style.ThemeMaterialNoActionBarFullscreen);
        _dialog.SetContentView(layout, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        // Il tasto Indietro non deve togliere la copertura.
        _dialog.SetCancelable(false);
        _dialog.Window?.AddFlags(WindowManagerFlags.Secure);
        _dialog.Show();
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
        _dialog?.Dismiss();
        _dialog?.Dispose();
        _dialog = null;
        _message = null;
    });
}
