using ClinicalSensorFusion.Core.Domain;
using Microsoft.Maui.Controls.Shapes;
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace ClinicalSensorFusion.App.Controls;

/// <summary>Tessera colorata con il pittogramma della modalità (corpo, mani, volto).</summary>
public sealed class ModeBadge : ContentView
{
    public static readonly BindableProperty ModeProperty = BindableProperty.Create(
        nameof(Mode), typeof(CaptureMode), typeof(ModeBadge), CaptureMode.BodyLidar,
        propertyChanged: (b, _, _) => ((ModeBadge)b).Refresh());

    public static readonly BindableProperty SizeProperty = BindableProperty.Create(
        nameof(Size), typeof(double), typeof(ModeBadge), 44d,
        propertyChanged: (b, _, _) => ((ModeBadge)b).Refresh());

    /// <summary>Su fondi scuri (intestazioni, anteprima): tessera traslucida e pittogramma bianco.</summary>
    public static readonly BindableProperty OnDarkProperty = BindableProperty.Create(
        nameof(OnDark), typeof(bool), typeof(ModeBadge), false,
        propertyChanged: (b, _, _) => ((ModeBadge)b).Refresh());

    private static readonly PathGeometryConverter Geometry = new();
    private readonly Border _tile = new() { StrokeThickness = 0 };
    private readonly Path _glyph = new()
    {
        StrokeThickness = 2,
        StrokeLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Aspect = Stretch.Uniform,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
    };

    public ModeBadge()
    {
        _tile.Content = _glyph;
        Content = _tile;
        Refresh();
    }

    public CaptureMode Mode
    {
        get => (CaptureMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public bool OnDark
    {
        get => (bool)GetValue(OnDarkProperty);
        set => SetValue(OnDarkProperty, value);
    }

    // Toni medi: leggibili sia sul fondo chiaro sia su quello scuro.
    public static Color ColorFor(CaptureMode mode) => Color.FromArgb(mode switch
    {
        CaptureMode.HandsLidar => "#8B5CF6",
        CaptureMode.FaceTrueDepth => "#EC4899",
        _ => "#3B82F6",
    });

    // Pittogrammi su griglia 24×24, solo tratto.
    private static string GlyphFor(CaptureMode mode) => mode switch
    {
        CaptureMode.HandsLidar => "M7,12 V6 M10.3,11 V3.5 M13.7,11 V3.5 M17,12 V6 M7,12 V15 C7,22 17,22 17,15 V12 M17,14.5 L20.5,11",
        CaptureMode.FaceTrueDepth => "M12,3 C24,3 24,21 12,21 C0,21 0,3 12,3 Z M9,10 V10.6 M15,10 V10.6 M8.5,14.5 C10.5,16.8 13.5,16.8 15.5,14.5",
        _ => "M12,2.5 C14.6,2.5 14.6,6.5 12,6.5 C9.4,6.5 9.4,2.5 12,2.5 Z M5,10 H19 M12,10 V15.5 M12,15.5 L8,22 M12,15.5 L16,22",
    };

    private void Refresh()
    {
        var color = ColorFor(Mode);
        _tile.WidthRequest = _tile.HeightRequest = Size;
        _tile.StrokeShape = new RoundRectangle { CornerRadius = Size * 0.3 };
        _tile.BackgroundColor = OnDark ? Colors.White.WithAlpha(0.16f) : color.WithAlpha(0.16f);
        _glyph.Stroke = OnDark ? Colors.White : color;
        _glyph.WidthRequest = _glyph.HeightRequest = Size * 0.5;
        _glyph.Data = (Geometry)Geometry.ConvertFromInvariantString(GlyphFor(Mode))!;
    }
}
