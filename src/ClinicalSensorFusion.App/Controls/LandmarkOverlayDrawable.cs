using ClinicalSensorFusion.App.Services;

namespace ClinicalSensorFusion.App.Controls;

/// <summary>
/// Disegna scheletro / mani / volto sopra l'anteprima. I punti arrivano già in coordinate normalizzate
/// della vista; qui si scala alla dimensione del GraphicsView (stessa area dell'anteprima).
/// Verde = profondità LiDAR valida, giallo = solo immagine (stima meno affidabile).
/// </summary>
public sealed class LandmarkOverlayDrawable : IDrawable
{
    private static readonly Color LidarColor = Color.FromArgb("#34D399");
    private static readonly Color ImageColor = Color.FromArgb("#FBBF24");
    private static readonly Color HandColor = Color.FromArgb("#60A5FA");
    private static readonly Color FaceColor = Color.FromArgb("#F472B6");

    private volatile OverlayFrame? _frame;

    public void Update(OverlayFrame frame) => _frame = frame;

    public void Clear() => _frame = null;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var frame = _frame;
        if (frame is null || frame.Points.Count == 0)
        {
            return;
        }

        var w = dirtyRect.Width;
        var h = dirtyRect.Height;
        var points = frame.Points;

        canvas.StrokeSize = 4;
        canvas.StrokeLineCap = LineCap.Round;
        foreach (var (from, to) in frame.Connections)
        {
            if (from >= points.Count || to >= points.Count || !points[from].Valid || !points[to].Valid)
            {
                continue;
            }

            canvas.StrokeColor = ColorFor(points[from].Kind == OverlayPointKind.Lidar && points[to].Kind == OverlayPointKind.Lidar
                ? OverlayPointKind.Lidar
                : points[from].Kind);
            canvas.DrawLine(points[from].X * w, points[from].Y * h, points[to].X * w, points[to].Y * h);
        }

        var radius = points.Count > 100 ? 1.5f : 5f;
        foreach (var p in points)
        {
            if (!p.Valid)
            {
                continue;
            }

            canvas.FillColor = ColorFor(p.Kind);
            canvas.FillCircle(p.X * w, p.Y * h, radius);
        }
    }

    private static Color ColorFor(OverlayPointKind kind) => kind switch
    {
        OverlayPointKind.Lidar => LidarColor,
        OverlayPointKind.Hand => HandColor,
        OverlayPointKind.Face => FaceColor,
        _ => ImageColor,
    };
}
