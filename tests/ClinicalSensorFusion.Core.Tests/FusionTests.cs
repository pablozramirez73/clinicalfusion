using System.Numerics;
using ClinicalSensorFusion.Core.Fusion;

namespace ClinicalSensorFusion.Core.Tests;

public class JointAngleCalculatorTests
{
    [Fact]
    public void Right_angle_is_90_degrees()
    {
        var angle = JointAngleCalculator.InteriorAngleDegrees(new Vector3(1, 0, 0), Vector3.Zero, new Vector3(0, 1, 0));
        Assert.Equal(90f, angle, 0.01f);
    }

    [Fact]
    public void Degenerate_segment_gives_NaN()
    {
        Assert.True(float.IsNaN(JointAngleCalculator.InteriorAngleDegrees(Vector3.Zero, Vector3.Zero, Vector3.UnitX)));
        Assert.True(float.IsNaN(JointAngleCalculator.InteriorAngleDegrees(new Vector3(float.NaN), Vector3.Zero, Vector3.UnitX)));
    }

    [Fact]
    public void Straight_knee_has_zero_flexion_and_bent_knee_ninety()
    {
        var p = Enumerable.Repeat(new Vector3(float.NaN), PoseLandmark.Count).ToArray();
        p[PoseLandmark.LeftHip] = new Vector3(0, 0, 2);
        p[PoseLandmark.LeftKnee] = new Vector3(0, 0.45f, 2);
        p[PoseLandmark.LeftAnkle] = new Vector3(0, 0.9f, 2);
        var straight = JointAngleCalculator.Compute(p);
        Assert.Equal(0f, straight[(int)Joint.LeftKneeFlexion], 0.01f);
        Assert.True(float.IsNaN(straight[(int)Joint.RightKneeFlexion]));

        p[PoseLandmark.LeftAnkle] = new Vector3(0, 0.45f, 2.45f);
        var bent = JointAngleCalculator.Compute(p);
        Assert.Equal(90f, bent[(int)Joint.LeftKneeFlexion], 0.01f);
    }
}

public class OrientationMapperTests
{
    [Theory]
    [InlineData(ImageOrientation.Up)]
    [InlineData(ImageOrientation.Right)]
    [InlineData(ImageOrientation.Left)]
    [InlineData(ImageOrientation.Down)]
    public void Round_trip_is_identity(ImageOrientation orientation)
    {
        var (u, v) = OrientationMapper.ToOriented(0.2f, 0.7f, orientation);
        var (x, y) = OrientationMapper.ToSensor(u, v, orientation);
        Assert.Equal(0.2f, x, 1e-6f);
        Assert.Equal(0.7f, y, 1e-6f);
    }

    [Fact]
    public void Portrait_top_center_maps_to_sensor_left_center()
    {
        // iPhone in verticale: la parte alta dell'immagine dritta corrisponde al lato sinistro del sensore landscape
        // (rotazione di 90° in senso orario).
        var (x, y) = OrientationMapper.ToSensor(0.5f, 0f, ImageOrientation.Right);
        Assert.Equal(0f, x, 1e-6f);
        Assert.Equal(0.5f, y, 1e-6f);
    }
}

public class DepthFusionTests
{
    private static DepthMap UniformDepth(float meters, int w = 256, int h = 192) =>
        new(w, h, Enumerable.Repeat(meters, w * h).ToArray(), Enumerable.Repeat((byte)2, w * h).ToArray());

    [Fact]
    public void Median_ignores_low_confidence_and_invalid_samples()
    {
        var depth = new float[9] { 1, 1, 1, 1, 1, 1, 50, float.NaN, 0 };
        var confidence = new byte[9] { 2, 2, 2, 2, 2, 2, 0, 2, 2 };
        var map = new DepthMap(3, 3, depth, confidence);
        Assert.Equal(1f, map.SampleMedian(0.5f, 0.5f, radius: 1));
        Assert.Equal(6f / 9f, map.ValidRatio(), 1e-6f);
    }

    [Fact]
    public void Principal_point_unprojects_on_optical_axis()
    {
        var k = new CameraIntrinsics(1500, 1500, 960, 720, 1920, 1440);
        var fusion = new PoseDepthFusion();
        var p = fusion.Unproject(new NormalizedLandmark(0.5f, 0.5f, 0, 1), ImageOrientation.Up, UniformDepth(2f), k);
        Assert.Equal(0f, p.X, 1e-4f);
        Assert.Equal(0f, p.Y, 1e-4f);
        Assert.Equal(2f, p.Z, 1e-4f);
    }

    [Fact]
    public void Offset_point_scales_with_depth_over_focal_length()
    {
        var k = new CameraIntrinsics(1500, 1500, 960, 720, 1920, 1440);
        var fusion = new PoseDepthFusion();
        // 0.75 × 1920 = 1440 px → 480 px dal centro → X = 480 · 3 / 1500 = 0.96 m.
        var p = fusion.Unproject(new NormalizedLandmark(0.75f, 0.5f, 0, 1), ImageOrientation.Up, UniformDepth(3f), k);
        Assert.Equal(0.96f, p.X, 1e-3f);
    }

    [Fact]
    public void Invisible_or_out_of_range_points_are_NaN()
    {
        var k = new CameraIntrinsics(1500, 1500, 960, 720, 1920, 1440);
        var fusion = new PoseDepthFusion();
        Assert.True(float.IsNaN(fusion.Unproject(new NormalizedLandmark(0.5f, 0.5f, 0, 0.1f), ImageOrientation.Up, UniformDepth(2f), k).Z));
        Assert.True(float.IsNaN(fusion.Unproject(new NormalizedLandmark(0.5f, 0.5f, 0, 1f), ImageOrientation.Up, UniformDepth(7f), k).Z));
    }

    [Fact]
    public void Falls_back_to_MediaPipe_world_landmarks_when_lidar_is_out_of_range()
    {
        var k = new CameraIntrinsics(1500, 1500, 960, 720, 1920, 1440);
        var landmarks = Enumerable.Repeat(new NormalizedLandmark(0.5f, 0.5f, 0, 1), PoseLandmark.Count).ToArray();
        var world = Enumerable.Range(0, PoseLandmark.Count).Select(i => new Vector3(i, 0, 0)).ToArray();
        var fused = new PoseDepthFusion().Fuse(landmarks, world, ImageOrientation.Right, UniformDepth(9f), k);
        Assert.Equal(PoseSource.MediaPipeWorld, fused.Source);
        Assert.Equal(0, fused.LidarJoints);
        Assert.Equal(new Vector3(5, 0, 0), fused.Points[5]);

        var near = new PoseDepthFusion().Fuse(landmarks, world, ImageOrientation.Right, UniformDepth(2f), k);
        Assert.Equal(PoseSource.Lidar, near.Source);
        Assert.Equal(2f, near.SubjectDistance, 1e-4f);
    }
}

public class SummaryReportTests
{
    [Fact]
    public void Rows_are_localized_and_flag_quality_issues()
    {
        var summary = new Domain.SessionSummary
        {
            DurationSeconds = 75,
            LidarCoverage = 0.4,
            JointRanges = new() { [Joint.LeftKneeFlexion] = new JointRange(5, 95, 40, 300) },
            DroppedSamples = 3,
            SampleCounts = new() { [Sensors.SensorKind.Motion] = 7500 },
        };

        var rows = Domain.SummaryReport.Build(summary, System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
        Assert.Contains(rows, r => r.Label == "Durata" && r.Value == "01:15");
        Assert.Contains(rows, r => r.Label == "Copertura LiDAR" && r.Warning);
        Assert.Contains(rows, r => r.Label == "Flessione ginocchio sx" && r.Value == "5°–95° (ROM 90°)");
        Assert.Contains(rows, r => r.Label == "Campioni persi" && r.Warning);
        Assert.Contains(rows, r => r.Value == "7.500 campioni");
    }
}
