using ClinicalSensorFusion.Core.Localization;
using Android.Hardware;
using ClinicalSensorFusion.Core.Sensors;

namespace ClinicalSensorFusion.App.Platforms.Android.Sensors;

/// <summary>
/// Accelerometro, giroscopio e magnetometro a 100 Hz, nello stesso layout di CMDeviceMotion su iOS.
/// Si usano i sensori "fusi" di Android (accelerazione lineare, gravità, vettore di rotazione): come
/// CoreMotion separano gravità e accelerazione dell'utente, indispensabile per il tremore.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Segno e unità: Android misura in m/s² la forza che sostiene il telefono (fermo e a faccia in su:
/// z = +9,81), iOS in g l'opposto (z = −1). Si converte alla convenzione iOS, quella dell'archivio.</item>
/// <item>Assetto: il riferimento di Android è geografico (X est, Y nord, Z in alto), quello di CoreMotion ha
/// l'asse X arbitrario. Gli angoli rispetto alla verticale coincidono; l'imbardata assoluta no.</item>
/// <item>Si emette un campione a ogni lettura dell'accelerazione, con l'ultimo valore degli altri sensori.</item>
/// </list>
/// </remarks>
internal sealed class MotionSensorSource(SensorClock clock) : AndroidSensorSource(clock)
{
    private const float StandardGravity = SensorManager.GravityEarth;

    // Senza i sensori fusi (telefoni senza giroscopio) la gravità si stima con un passa-basso sull'accelerometro.
    private static readonly bool Fused =
        Manager.GetDefaultSensor(SensorType.LinearAcceleration) is not null && Manager.GetDefaultSensor(SensorType.Gravity) is not null;

    private readonly float[] _rotation = [float.NaN, float.NaN, float.NaN];
    private readonly float[] _quaternion = [float.NaN, float.NaN, float.NaN, float.NaN];
    private readonly float[] _magnetic = [float.NaN, float.NaN, float.NaN];
    private readonly float[] _gravity = [float.NaN, float.NaN, float.NaN];

    public override string DisplayName => Loc.T("Accelerometro · giroscopio · magnetometro");

    public override IReadOnlyList<SensorKind> Produces { get; } = [SensorKind.Motion];

    public override bool IsAvailable => Manager.GetDefaultSensor(SensorType.Accelerometer) is not null;

    protected override IReadOnlyList<SensorType> Types { get; } = Fused
        ? [SensorType.LinearAcceleration, SensorType.Gravity, SensorType.Gyroscope, SensorType.RotationVector, SensorType.MagneticField]
        : [SensorType.Accelerometer, SensorType.Gyroscope, SensorType.RotationVector, SensorType.MagneticField];

    protected override int SamplingPeriodUs => 10_000;

    protected override void OnReading(Sensor sensor, IList<float> values, double time)
    {
        if (values.Count < 3)
        {
            return;
        }

        float x = values[0], y = values[1], z = values[2];
        switch (sensor.Type)
        {
            case SensorType.Gyroscope:
                (_rotation[0], _rotation[1], _rotation[2]) = (x, y, z);
                break;
            case SensorType.MagneticField:
                (_magnetic[0], _magnetic[1], _magnetic[2]) = (x, y, z);
                break;
            case SensorType.RotationVector:
                // Quaternione unitario: la parte scalare è il quarto valore, o si ricava dalle altre tre.
                var w = values.Count >= 4 ? values[3] : MathF.Sqrt(Math.Max(0, 1 - (x * x) - (y * y) - (z * z)));
                (_quaternion[0], _quaternion[1], _quaternion[2], _quaternion[3]) = (x, y, z, w);
                break;
            case SensorType.Gravity:
                (_gravity[0], _gravity[1], _gravity[2]) = (x, y, z);
                break;
            case SensorType.LinearAcceleration:
                Emit(x, y, z, time);
                break;
            case SensorType.Accelerometer:
                const float Smoothing = 0.9f;
                if (float.IsNaN(_gravity[0]))
                {
                    (_gravity[0], _gravity[1], _gravity[2]) = (x, y, z);
                }
                else
                {
                    _gravity[0] = (Smoothing * _gravity[0]) + ((1 - Smoothing) * x);
                    _gravity[1] = (Smoothing * _gravity[1]) + ((1 - Smoothing) * y);
                    _gravity[2] = (Smoothing * _gravity[2]) + ((1 - Smoothing) * z);
                }

                Emit(x - _gravity[0], y - _gravity[1], z - _gravity[2], time);
                break;
        }
    }

    private void Emit(float ax, float ay, float az, double time) =>
        Sink?.TryWrite(new SensorReading(SensorKind.Motion, time,
        [
            -ax / StandardGravity, -ay / StandardGravity, -az / StandardGravity,
            _rotation[0], _rotation[1], _rotation[2],
            _quaternion[0], _quaternion[1], _quaternion[2], _quaternion[3],
            _magnetic[0], _magnetic[1], _magnetic[2],
            -_gravity[0] / StandardGravity, -_gravity[1] / StandardGravity, -_gravity[2] / StandardGravity,
        ]));
}
