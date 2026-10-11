namespace ClinicalSensorFusion.Core.Fusion;

/// <summary>
/// Angoli articolari calcolati. L'ordine dei valori è il layout del flusso <c>JointAngles</c>:
/// NON riordinare.
/// </summary>
public enum Joint
{
    LeftShoulderElevation,
    RightShoulderElevation,
    LeftElbowFlexion,
    RightElbowFlexion,
    LeftHipFlexion,
    RightHipFlexion,
    LeftKneeFlexion,
    RightKneeFlexion,
    LeftAnkle,
    RightAnkle,
}

/// <summary>Escursione articolare (ROM) osservata durante la rilevazione, in gradi.</summary>
public sealed record JointRange(double Min, double Max, double Mean, long Samples)
{
    public double RangeOfMotion => Max - Min;
}
