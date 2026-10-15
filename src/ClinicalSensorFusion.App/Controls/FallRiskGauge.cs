using System.Globalization;

namespace ClinicalSensorFusion.App.Controls;

/// <summary>
/// Semicerchio del rischio di caduta: tre fasce (bassa, intermedia, alta) in tinta tenue, l'arco pieno fino alla
/// stima nel colore della sua fascia e la percentuale al centro. All'arrivo di un valore l'arco si riempie.
/// </summary>
public sealed class FallRiskGauge : GraphicsView
{
    public static readonly BindableProperty ProbabilityProperty = BindableProperty.Create(
        nameof(Probability), typeof(double), typeof(FallRiskGauge), 0d,
        propertyChanged: (b, _, value) => ((FallRiskGauge)b).AnimateTo((double)value));

    private readonly GaugeDrawable _drawable = new();

    public FallRiskGauge()
    {
        Drawable = _drawable;
        HeightRequest = 150;
        BackgroundColor = Colors.Transparent;
    }

    /// <summary>Probabilità 0–1; un valore non finito mostra solo le fasce, senza arco né percentuale.</summary>
    public double Probability
    {
        get => (double)GetValue(ProbabilityProperty);
        set => SetValue(ProbabilityProperty, value);
    }

    private void AnimateTo(double target)
    {
        this.AbortAnimation(nameof(FallRiskGauge));
        _drawable.Target = target;
        if (!double.IsFinite(target))
        {
            Invalidate();
            return;
        }

        new Animation(v =>
        {
            _drawable.Shown = v;
            Invalidate();
        }, 0, Math.Clamp(target, 0, 1), Easing.CubicOut).Commit(this, nameof(FallRiskGauge), length: 900);
    }

    private sealed class GaugeDrawable : IDrawable
    {
        private static readonly Color Ok = Color.FromArgb("#059669");
        private static readonly Color Warning = Color.FromArgb("#D97706");
        private static readonly Color Danger = Color.FromArgb("#DC2626");

        public double Target { get; set; } = double.NaN;

        public double Shown { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            const float stroke = 16;
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            var radius = Math.Min((rect.Width / 2) - stroke, rect.Height - stroke - 4);
            var center = new PointF(rect.Center.X, rect.Bottom - 6);

            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeSize = stroke;

            // Fasce di sfondo, separate da un piccolo spazio.
            (double From, double To, Color Color)[] bands = [(0, 1d / 3, Ok), (1d / 3, 2d / 3, Warning), (2d / 3, 1, Danger)];
            foreach (var (from, to, band) in bands)
            {
                canvas.StrokeColor = band.WithAlpha(dark ? 0.28f : 0.18f);
                canvas.DrawPath(Arc(center, radius, from + 0.012, to - 0.012));
            }

            if (!double.IsFinite(Target))
            {
                return;
            }

            var shown = Math.Clamp(Shown, 0.0001, 1);
            var color = ColorFor(shown);
            canvas.StrokeColor = color;
            canvas.DrawPath(Arc(center, radius, 0, shown));

            // Indicatore sulla punta dell'arco.
            var tip = PointAt(center, radius, shown);
            canvas.FillColor = dark ? Color.FromArgb("#0B1220") : Colors.White;
            canvas.FillCircle(tip, (stroke / 2) + 3);
            canvas.FillColor = color;
            canvas.FillCircle(tip, (stroke / 2) - 3);

            canvas.FontColor = color;
            canvas.Font = Microsoft.Maui.Graphics.Font.DefaultBold;
            canvas.FontSize = 40;
            var text = shown.ToString("P0", CultureInfo.CurrentCulture);
            canvas.DrawString(text, center.X - radius, center.Y - 58, radius * 2, 50, HorizontalAlignment.Center, VerticalAlignment.Bottom);
        }

        private static Color ColorFor(double p) => p < 1d / 3 ? Ok : p < 2d / 3 ? Warning : Danger;

        // L'arco va da sinistra (0) a destra (1) passando per l'alto.
        private static PointF PointAt(PointF center, float radius, double p)
        {
            var angle = Math.PI * (1 - p);
            return new PointF(center.X + (radius * (float)Math.Cos(angle)), center.Y - (radius * (float)Math.Sin(angle)));
        }

        private static PathF Arc(PointF center, float radius, double from, double to)
        {
            var path = new PathF(PointAt(center, radius, from));
            var steps = Math.Max(2, (int)((to - from) * 90));
            for (var i = 1; i <= steps; i++)
            {
                path.LineTo(PointAt(center, radius, from + ((to - from) * i / steps)));
            }

            return path;
        }
    }
}
