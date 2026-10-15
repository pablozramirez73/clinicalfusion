using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Signal;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Modello del rischio di caduta incorporato e passi riconosciuti dalla posa.</summary>
public class FallRiskTests
{
    private static ClinicalSession Session(ClinicalTest test, double value, DateTimeOffset at, PoseGaitSummary? gait = null) => new()
    {
        Id = ClinicalSession.NewId(),
        SubjectCode = "S01",
        OperatorId = "OP",
        OperatorName = "Operatore",
        Mode = CaptureMode.BodyLidar,
        StartedAt = at,
        EndedAt = at.AddMinutes(1),
        Summary = new SessionSummary
        {
            Mode = CaptureMode.BodyLidar,
            PoseGait = gait,
            Test = new TestOutcome(test.Id, test.Name, test.ResultKind, value, false, false, string.Empty),
        },
    };

    // Probabilità calcolate da scikit-learn (predict_proba) sullo stesso modello: ml/fall_risk/gstride_train.py.
    [Fact]
    public void Embedded_model_matches_scikit_learn()
    {
        var model = FallRiskModel.Default;
        Assert.Equal(["tug_time_s", "gait_speed_m_s", "sppb_chair_index", "sppb_balance_index", "cadence_strides_min", "stride_time_s", "stride_time_cv"], model.Features);
        var complete = new Dictionary<string, double>
        {
            ["tug_time_s"] = 14.0, ["gait_speed_m_s"] = 0.7, ["sppb_chair_index"] = 2, ["sppb_balance_index"] = 3,
            ["cadence_strides_min"] = 48.0, ["stride_time_s"] = 1.25, ["stride_time_cv"] = 0.06,
        };
        Assert.Equal(0.563715, model.Probability(complete), 4); // i pesi nel JSON hanno 6 decimali

        // Le misure assenti prendono la mediana di GSTRIDE, come l'imputazione in Python.
        var partial = new Dictionary<string, double> { ["tug_time_s"] = 11.0, ["sppb_balance_index"] = 4, ["gait_speed_m_s"] = double.NaN };
        Assert.Equal(0.419752, model.Probability(partial), 4);
    }

    [Fact]
    public void Slower_and_less_steady_subjects_get_a_higher_estimate()
    {
        var model = FallRiskModel.Default;
        var fit = new Dictionary<string, double> { ["tug_time_s"] = 8, ["gait_speed_m_s"] = 1.2, ["sppb_balance_index"] = 4 };
        var frail = new Dictionary<string, double> { ["tug_time_s"] = 25, ["gait_speed_m_s"] = 0.4, ["sppb_balance_index"] = 2 };
        Assert.True(model.Probability(frail) > model.Probability(fit) + 0.3);
    }

    [Fact]
    public void Estimate_uses_the_latest_run_of_each_test_and_converts_like_python()
    {
        var t0 = DateTimeOffset.Parse("2026-10-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var sessions = new[]
        {
            Session(ClinicalTests.TimedUpAndGo, 20, t0),
            Session(ClinicalTests.TimedUpAndGo, 12.5, t0.AddDays(7)), // più recente: vale questa
            Session(ClinicalTests.TenMeterWalk, 0.9, t0, new PoseGaitSummary(14, 100, 0.05)),
            Session(ClinicalTests.FiveTimesSitToStand, 14, t0),
        };

        var estimate = FallRisk.Estimate(sessions)!;
        Assert.Equal(12.5, estimate.Inputs["tug_time_s"]);
        Assert.Equal(0.9, estimate.Inputs["gait_speed_m_s"]);
        Assert.Equal(2, estimate.Inputs["sppb_chair_index"]); // 13,7–16,69 s
        Assert.Equal(50, estimate.Inputs["cadence_strides_min"]); // 100 passi/min = 50 falcate/min
        Assert.Equal(1.2, estimate.Inputs["stride_time_s"], 6);
        Assert.False(estimate.Inputs.ContainsKey("sppb_balance_index"));
        Assert.Equal([ClinicalTests.TandemStance], estimate.TestsMissing);
        Assert.Equal(FallRiskModel.Default.Probability(estimate.Inputs), estimate.Probability);
    }

    [Fact]
    public void No_estimate_from_too_few_tests_or_without_tug_and_walk()
    {
        var t0 = DateTimeOffset.UtcNow;
        Assert.Null(FallRisk.Estimate([Session(ClinicalTests.TimedUpAndGo, 10, t0)]));
        Assert.Null(FallRisk.Estimate([Session(ClinicalTests.FiveTimesSitToStand, 12, t0), Session(ClinicalTests.TandemStance, 10, t0)]));
        Assert.NotNull(FallRisk.Estimate([Session(ClinicalTests.TenMeterWalk, 1, t0), Session(ClinicalTests.TandemStance, 10, t0)]));
    }

    [Theory]
    [InlineData(10.0, 4)]
    [InlineData(12.0, 3)]
    [InlineData(15.0, 2)]
    [InlineData(30.0, 1)]
    [InlineData(61.0, 0)]
    public void Chair_stand_time_maps_to_sppb_points(double seconds, double points) =>
        Assert.Equal(points, FallRisk.SppbChairIndex(seconds));

    [Theory]
    [InlineData(10.0, 4)]
    [InlineData(5.0, 3)]
    [InlineData(1.0, 2)]
    public void Tandem_time_maps_to_sppb_balance_points(double seconds, double points) =>
        Assert.Equal(points, FallRisk.SppbBalanceIndex(seconds));

    [Fact]
    public void Steps_are_counted_from_the_distance_between_the_ankles()
    {
        // Cammino simulato a 30 fps: le caviglie si allontanano e si avvicinano 1,8 volte al secondo,
        // cioè un passo ogni 0,556 s → 108 passi al minuto.
        var steps = new PoseStepAnalyzer();
        for (var i = 0; i < 300; i++)
        {
            var t = i / 30.0;
            var stride = 0.15 + (0.25 * Math.Abs(Math.Sin(Math.PI * 1.8 * t)));
            steps.Add(t, Pose(stride));
        }

        var gait = steps.Build()!;
        // A 30 fotogrammi al secondo un passo da 0,556 s dura 16 o 17 fotogrammi: ±4 passi/min.
        Assert.InRange(gait.CadenceStepsPerMinute, 104, 112);
        Assert.True(gait.StepTimeCv < 0.05);
        Assert.InRange(gait.Steps, 16, 19);
    }

    [Fact]
    public void Standing_still_gives_no_steps()
    {
        var steps = new PoseStepAnalyzer();
        for (var i = 0; i < 300; i++)
        {
            steps.Add(i / 30.0, Pose(0.2 + (0.002 * Math.Sin(i))));
        }

        Assert.Null(steps.Build());
    }

    /// <summary>Posa in piedi con le caviglie distanti <paramref name="ankleGap"/> m lungo la direzione di marcia.</summary>
    private static float[] Pose(double ankleGap)
    {
        var p = new float[PoseLandmark.Count * 3];
        void Set(int i, double x, double y, double z) => (p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]) = ((float)x, (float)y, (float)z);
        Set(PoseLandmark.LeftHip, -0.1, 0, 0);
        Set(PoseLandmark.RightHip, 0.1, 0, 0);
        Set(PoseLandmark.LeftKnee, -0.1, 0.45, 0);
        Set(PoseLandmark.RightKnee, 0.1, 0.45, 0);
        Set(PoseLandmark.LeftAnkle, -0.1, 0.9, ankleGap / 2);
        Set(PoseLandmark.RightAnkle, 0.1, 0.9, -ankleGap / 2);
        return p;
    }
}
