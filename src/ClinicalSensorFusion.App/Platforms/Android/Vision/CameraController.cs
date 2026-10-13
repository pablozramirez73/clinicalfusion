using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Localization;
using Java.Util.Concurrent;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Platforms.Android.Vision;

/// <summary>
/// Fotocamera con CameraX: anteprima sulla <see cref="PreviewView"/> e fotogrammi RGBA per MediaPipe.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Niente profondità: i telefoni Android non hanno un equivalente diffuso del LiDAR, quindi la
/// modalità corpo usa sempre la stima 3D di MediaPipe.</item>
/// <item>Back-pressure: CameraX consegna un fotogramma alla volta e scarta quelli che arrivano mentre
/// l'analisi è in corso (<c>STRATEGY_KEEP_ONLY_LATEST</c>). L'inferenza gira quindi direttamente nel
/// thread dell'analisi: conta l'attualità del dato, non elaborare ogni fotogramma in ritardo.</item>
/// <item>Anteprima e analisi usano lo stesso rapporto 4:3 (il default di CameraX): inquadrano la stessa
/// area, e i punti si riportano sulla vista con il solo ritaglio "riempi" dell'anteprima.</item>
/// <item>Il ciclo di vita è quello dell'attività: in background il sistema chiude la camera da sé.</item>
/// </list>
/// </remarks>
internal sealed class CameraController(VisionPipeline vision, ILogger logger) : Java.Lang.Object, ImageAnalysis.IAnalyzer
{
    private readonly IExecutorService _analysisExecutor = Executors.NewSingleThreadExecutor()!;
    private ProcessCameraProvider? _provider;
    private PreviewView? _previewView;
    private bool _wanted;
    private bool _front;
    private volatile int _viewWidth;
    private volatile int _viewHeight;

    public CaptureMode Mode { get; private set; }

    /// <summary>La camera è collegata e sta consegnando fotogrammi (ciclo di vita permettendo).</summary>
    public bool IsRunning { get; private set; }

    public static bool HasFrontCamera =>
        global::Android.App.Application.Context.PackageManager?.HasSystemFeature(PackageManager.FeatureCameraFront) == true;

    /// <summary>Vista di anteprima creata dall'handler MAUI (null quando la pagina viene smontata).</summary>
    public void AttachPreview(PreviewView? view)
    {
        if (_previewView is not null)
        {
            _previewView.LayoutChange -= OnPreviewLayout;
        }

        _previewView = view;
        if (view is not null)
        {
            view.LayoutChange += OnPreviewLayout;
            (_viewWidth, _viewHeight) = (view.Width, view.Height);
        }

        Rebind();
    }

    public Task StartAsync(CaptureMode mode)
    {
        if (mode == CaptureMode.FaceTrueDepth && !HasFrontCamera)
        {
            throw new NotSupportedException(Loc.T("Questo telefono non ha una fotocamera frontale."));
        }

        vision.Configure(mode);
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            Mode = mode;
            _front = mode == CaptureMode.FaceTrueDepth;
            _wanted = true;
            if (_provider is not null)
            {
                Rebind();
                return;
            }

            var context = global::Android.App.Application.Context;
            var future = ProcessCameraProvider.GetInstance(context);
            future.AddListener(new Java.Lang.Runnable(() =>
            {
                try
                {
                    _provider = (ProcessCameraProvider)future.Get()!;
                    Rebind();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "CameraX non disponibile.");
                }
            }), ContextCompat.GetMainExecutor(context));
        });
    }

    public Task StopAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
        _wanted = false;
        Rebind();
    });

    /// <summary>Chiamato da CameraX sul thread dell'analisi, un fotogramma alla volta.</summary>
    public void Analyze(IImageProxy? image)
    {
        if (image is null)
        {
            return;
        }

        Bitmap? bitmap = null;
        try
        {
            var info = image.ImageInfo!;
            var rotation = info.RotationDegrees;
            var timestamp = info.Timestamp;
            bitmap = image.ToBitmap()!;
            // Il fotogramma va restituito subito a CameraX: da qui in poi si lavora sulla copia.
            image.Close();
            vision.Process(new CameraFrame(bitmap, rotation, _front, Uptime(timestamp), _viewWidth, _viewHeight));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Errore nell'elaborazione del fotogramma.");
        }
        finally
        {
            image.Close();
            bitmap?.Recycle();
            bitmap?.Dispose();
            image.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _provider?.UnbindAll();
            _analysisExecutor.Shutdown();
        }

        base.Dispose(disposing);
    }

    /// <summary>Ricollega i casi d'uso allo stato corrente. Solo sul thread principale.</summary>
    private void Rebind()
    {
        if (_provider is not { } provider)
        {
            return;
        }

        provider.UnbindAll();
        IsRunning = false;
        if (!_wanted || Platform.CurrentActivity is not ILifecycleOwner owner)
        {
            return;
        }

        try
        {
            var analysis = new ImageAnalysis.Builder()
                .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)!
                .SetOutputImageFormat(ImageAnalysis.OutputImageFormatRgba8888)!
                .Build()!;
            analysis.SetAnalyzer(_analysisExecutor, this);

            var selector = (_front ? CameraSelector.DefaultFrontCamera : CameraSelector.DefaultBackCamera)!;
            if (_previewView is { } view)
            {
                var preview = new Preview.Builder().Build()!;
                preview.SetSurfaceProvider(ContextCompat.GetMainExecutor(view.Context!), view.SurfaceProvider);
                provider.BindToLifecycle(owner, selector, preview, analysis);
            }
            else
            {
                provider.BindToLifecycle(owner, selector, analysis);
            }

            IsRunning = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Avvio della fotocamera fallito.");
        }
    }

    private void OnPreviewLayout(object? sender, global::Android.Views.View.LayoutChangeEventArgs e) =>
        (_viewWidth, _viewHeight) = (e.Right - e.Left, e.Bottom - e.Top);

    /// <summary>
    /// Il timestamp della camera è in nanosecondi dall'avvio sui sensori "realtime" (quasi tutti); dove usa
    /// un'altra base il valore è implausibile e si ripiega sull'ora di arrivo.
    /// </summary>
    private static double Uptime(long sensorNanos)
    {
        var now = SystemClock.ElapsedRealtimeNanos() / 1e9;
        var uptime = sensorNanos / 1e9;
        return Math.Abs(now - uptime) < 1 ? uptime : now;
    }
}

/// <summary>Un fotogramma pronto per l'inferenza. La bitmap resta di proprietà di chi lo crea.</summary>
/// <param name="RotationDegrees">Rotazione oraria per vedere l'immagine dritta.</param>
/// <param name="Timestamp">Secondi dall'avvio del sistema.</param>
/// <param name="ViewWidth">Larghezza in pixel della vista di anteprima (0 se non c'è).</param>
internal readonly record struct CameraFrame(Bitmap Image, int RotationDegrees, bool FrontCamera, double Timestamp, int ViewWidth, int ViewHeight);
