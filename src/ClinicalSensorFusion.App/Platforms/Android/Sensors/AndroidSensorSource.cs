using Android.Content;
using Android.Hardware;
using Android.OS;
using Android.Runtime;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.App.Platforms.Android.Sensors;

/// <summary>
/// Base delle sorgenti lette dal SensorManager di Android: registra i sensori richiesti su un thread
/// dedicato (mai il thread dell'interfaccia) e converte il timestamp dell'evento nel tempo di sessione.
/// </summary>
internal abstract class AndroidSensorSource(SensorClock clock) : Java.Lang.Object, ISensorEventListener, ISensorSource
{
    private HandlerThread? _thread;

    public abstract string DisplayName { get; }

    public abstract IReadOnlyList<SensorKind> Produces { get; }

    public virtual bool IsAvailable => Types.Count > 0 && Manager.GetDefaultSensor(Types[0]) is not null;

    protected static SensorManager Manager { get; } =
        (SensorManager)global::Android.App.Application.Context.GetSystemService(Context.SensorService)!;

    protected SensorClock Clock => clock;

    protected ISensorSink? Sink { get; private set; }

    /// <summary>Sensori da registrare; il primo è quello indispensabile.</summary>
    protected abstract IReadOnlyList<SensorType> Types { get; }

    /// <summary>Intervallo di campionamento richiesto, in microsecondi (indicativo: decide il sistema).</summary>
    protected virtual int SamplingPeriodUs => 200_000;

    public virtual Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        Sink = sink;
        _thread = new HandlerThread("csf.sensors");
        _thread.Start();
        var handler = new Handler(_thread.Looper!);
        foreach (var type in Types)
        {
            if (Manager.GetDefaultSensor(type) is { } sensor)
            {
                Manager.RegisterListener(this, sensor, (SensorDelay)SamplingPeriodUs, handler);
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task StopAsync()
    {
        Manager.UnregisterListener(this);
        _thread?.QuitSafely();
        _thread = null;
        Sink = null;
        return Task.CompletedTask;
    }

    public void OnAccuracyChanged(Sensor? sensor, [GeneratedEnum] SensorStatus accuracy)
    {
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Sensor is { } sensor && e.Values is { } values)
        {
            OnReading(sensor, values, SessionTime(e.Timestamp));
        }
    }

    protected abstract void OnReading(Sensor sensor, IList<float> values, double time);

    /// <summary>
    /// I timestamp dei sensori sono nanosecondi dall'avvio (stessa base di <c>elapsedRealtime</c>) e
    /// riflettono l'istante della misura, non quello ritardato della callback. Qualche dispositivo usa
    /// però un'altra base: se il valore è implausibile si ripiega sull'ora di arrivo.
    /// </summary>
    protected double SessionTime(long eventNanos)
    {
        var uptime = eventNanos / 1e9;
        var now = SystemClock.ElapsedRealtimeNanos() / 1e9;
        return Math.Abs(now - uptime) < 1 ? clock.FromUptime(uptime) : clock.Now;
    }
}
