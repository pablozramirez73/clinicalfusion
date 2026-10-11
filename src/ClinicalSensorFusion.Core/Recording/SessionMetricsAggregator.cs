using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Recording;

/// <summary>Valori istantanei mostrati durante l'acquisizione.</summary>
public sealed record LiveMetrics(
    double Elapsed,
    float[]? JointAngles,
    float? SubjectDistance,
    float? AudioRmsDbfs,
    float? RelativeAltitude,
    long Steps,
    float? AmbientLux,
    bool ProximityNear,
    int ThermalState,
    IReadOnlyDictionary<SensorKind, long> Counts);

/// <summary>
/// Accumula gli indicatori clinici mentre i campioni scorrono. Non è thread-safe: viene usato solo dal
/// consumer di <see cref="SessionRecorder"/> (singolo lettore del canale); gli snapshot sono immutabili.
/// </summary>
public sealed class SessionMetricsAggregator
{
    private readonly CaptureMode _mode;
    private readonly RunningStats[] _joints = new RunningStats[JointAngleCalculator.JointCount];
    private readonly Dictionary<SensorKind, long> _counts = [];
    private readonly TremorAnalyzer _tremor = new();
    private readonly List<float> _distances = [];

    private double _elapsed;
    private float[]? _lastAngles;
    private float? _lastDistance;
    private float? _lastAudio;
    private float? _lastAltitude;
    private float? _lastLux;
    private bool _proximity;
    private int _thermal;
    private float[]? _lastPedometer;
    private long _poseFrames;
    private long _lidarPoseFrames;

    public SessionMetricsAggregator(CaptureMode mode) => _mode = mode;

    /// <summary>Aggiorna con un campione. Le metriche audio complete arrivano da <see cref="AudioFeatureAccumulator"/>.</summary>
    public void Add(in SensorReading r)
    {
        _counts[r.Kind] = _counts.GetValueOrDefault(r.Kind) + 1;
        _elapsed = Math.Max(_elapsed, r.Timestamp);
        var v = r.Values;

        switch (r.Kind)
        {
            case SensorKind.Motion when v.Length >= 3:
                _tremor.Add(v[0], v[1], v[2]);
                break;
            case SensorKind.Barometer when v.Length >= 1:
                _lastAltitude = v[0];
                break;
            case SensorKind.Pedometer when v.Length >= 5:
                _lastPedometer = v;
                break;
            case SensorKind.Proximity when v.Length >= 1:
                _proximity = v[0] > 0.5f;
                break;
            case SensorKind.AmbientLight when v.Length >= 1:
                _lastLux = v[0];
                break;
            case SensorKind.DeviceState when v.Length >= 1:
                _thermal = (int)v[0];
                break;
            case SensorKind.AudioLevel when v.Length >= 1:
                _lastAudio = v[0];
                break;
            case SensorKind.JointAngles:
                // Layout: angoli × JointCount, poi [sorgente (0=LiDAR,1=MediaPipe), distanza soggetto].
                _poseFrames++;
                for (var i = 0; i < _joints.Length && i < v.Length; i++)
                {
                    _joints[i].Add(v[i]);
                }

                _lastAngles = v[.._joints.Length];
                if (v.Length > _joints.Length + 1)
                {
                    if (v[_joints.Length] == 0)
                    {
                        _lidarPoseFrames++;
                    }

                    var d = v[_joints.Length + 1];
                    if (float.IsFinite(d))
                    {
                        _lastDistance = d;
                        _distances.Add(d);
                    }
                }

                break;
        }
    }

    public LiveMetrics Snapshot() => new(
        _elapsed,
        _lastAngles,
        _lastDistance,
        _lastAudio,
        _lastAltitude,
        _lastPedometer is { } p ? (long)p[0] : 0,
        _lastLux,
        _proximity,
        _thermal,
        new Dictionary<SensorKind, long>(_counts));

    public SessionSummary Build(double durationSeconds, AudioFeatures? audio, long dropped)
    {
        var ranges = new Dictionary<Joint, JointRange>();
        for (var i = 0; i < _joints.Length; i++)
        {
            if (_joints[i].Count > 0)
            {
                ranges[(Joint)i] = new JointRange(_joints[i].Min, _joints[i].Max, _joints[i].Mean, _joints[i].Count);
            }
        }

        GaitSummary? gait = null;
        if (_lastPedometer is { } ped)
        {
            var cadence = float.IsFinite(ped[2]) && ped[2] > 0 ? ped[2] * 60.0 : (double?)null;
            gait = new GaitSummary((long)ped[0], ped[1], cadence, (int)ped[3], (int)ped[4]);
        }

        double? medianDistance = null;
        if (_distances.Count > 0)
        {
            _distances.Sort();
            medianDistance = _distances[_distances.Count / 2];
        }

        return new SessionSummary
        {
            DurationSeconds = durationSeconds,
            Mode = _mode,
            JointRanges = ranges,
            LidarCoverage = _poseFrames > 0 ? _lidarPoseFrames / (double)_poseFrames : null,
            Tremor = _tremor.Analyze(),
            Gait = gait,
            Audio = audio,
            MedianSubjectDistanceMeters = medianDistance,
            SampleCounts = new Dictionary<SensorKind, long>(_counts),
            DroppedSamples = dropped,
        };
    }
}
