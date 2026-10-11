using ClinicalSensorFusion.Core.Sensors;
using CoreMotion;
using Foundation;

namespace ClinicalSensorFusion.App.Platforms.iOS.Sensors;

/// <summary>
/// Accelerometro, giroscopio e magnetometro fusi da CoreMotion (CMDeviceMotion) a 100 Hz.
/// Si usa il device motion e non i sensori grezzi: CoreMotion separa gravità e accelerazione utente
/// (indispensabile per il tremore) e calibra il magnetometro.
/// </summary>
internal sealed class MotionSensorSource(SensorClock clock) : ISensorSource
{
    private const double RateHz = 100;

    private readonly CMMotionManager _manager = new();
    private readonly NSOperationQueue _queue = new()
    {
        Name = "csf.motion",
        MaxConcurrentOperationCount = 1,
        QualityOfService = NSQualityOfService.UserInitiated,
    };

    public string DisplayName => "Accelerometro · giroscopio · magnetometro";

    public IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Motion];

    public bool IsAvailable => _manager.DeviceMotionAvailable;

    public Task StartAsync(ISensorSink sink, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return Task.CompletedTask;
        }

        // Riferimento con magnetometro → campo magnetico calibrato e imbardata senza deriva.
        var frame = CMMotionManager.AvailableAttitudeReferenceFrames.HasFlag(CMAttitudeReferenceFrame.XArbitraryCorrectedZVertical)
            ? CMAttitudeReferenceFrame.XArbitraryCorrectedZVertical
            : CMAttitudeReferenceFrame.XArbitraryZVertical;

        _manager.DeviceMotionUpdateInterval = 1.0 / RateHz;
        _manager.StartDeviceMotionUpdates(frame, _queue, (motion, error) =>
        {
            if (motion is null)
            {
                return;
            }

            var a = motion.UserAcceleration;
            var r = motion.RotationRate;
            var q = motion.Attitude.Quaternion;
            var m = motion.MagneticField.Field;
            var g = motion.Gravity;
            sink.TryWrite(new SensorReading(SensorKind.Motion, clock.FromUptime(motion.Timestamp),
            [
                (float)a.X, (float)a.Y, (float)a.Z,
                (float)r.x, (float)r.y, (float)r.z,
                (float)q.x, (float)q.y, (float)q.z, (float)q.w,
                (float)m.X, (float)m.Y, (float)m.Z,
                (float)g.X, (float)g.Y, (float)g.Z,
            ]));
        });

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _manager.StopDeviceMotionUpdates();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _manager.Dispose();
        _queue.Dispose();
    }
}
