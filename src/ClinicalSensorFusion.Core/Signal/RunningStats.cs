namespace ClinicalSensorFusion.Core.Signal;

/// <summary>Minimo, massimo e media incrementali (algoritmo di Welford per la media), ignorando NaN.</summary>
public struct RunningStats
{
    public long Count { get; private set; }

    public double Min { get; private set; }

    public double Max { get; private set; }

    public double Mean { get; private set; }

    public void Add(double value)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        if (Count == 0)
        {
            Min = Max = Mean = value;
            Count = 1;
            return;
        }

        Count++;
        Mean += (value - Mean) / Count;
        if (value < Min)
        {
            Min = value;
        }

        if (value > Max)
        {
            Max = value;
        }
    }
}
