using System.Numerics;
using System.Security.Claims;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Fusion;
using ClinicalSensorFusion.Core.Recording;
using ClinicalSensorFusion.Core.Sensors;
using ClinicalSensorFusion.Core.Signal;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Demo;

/// <summary>
/// Popola un database vuoto con pazienti e rilevazioni <b>fittizi</b> (solo con <c>Demo:Seed=true</c>, mai in
/// produzione). I dati passano dallo stesso percorso dell'app reale: aggregatore del Core → contratto di
/// sincronizzazione → <see cref="IngestService"/>. Così la demo esercita anche il codice di produzione.
/// </summary>
public sealed class DemoDataSeeder(ClinicalDbContext db, IngestService ingest, TimeProvider clock, ILogger<DemoDataSeeder> logger)
{
    private sealed record DemoPatient(string Code, string First, string Last, DateOnly Birth, Sex Sex, string Diagnosis,
        float KneeStartDeg, float KneeGainDeg, float Asymmetry, double TremorHz, double TremorG);

    private static readonly DemoPatient[] Patients =
    [
        new("DEMO-001", "Giulia", "Esposito", new(1958, 3, 14), Sex.Female, "Protesi totale di ginocchio dx (riabilitazione)", 55, 45, 0.0f, 0, 0),
        new("DEMO-002", "Marco", "Bianchi", new(1949, 11, 2), Sex.Male, "Malattia di Parkinson, stadio H&Y 2", 70, 10, 0.08f, 4.8, 0.035),
        new("DEMO-003", "Anna", "Colombo", new(1971, 6, 21), Sex.Female, "Tremore essenziale", 95, 5, 0.0f, 7.5, 0.02),
        new("DEMO-004", "Luca", "Ferrari", new(1985, 1, 9), Sex.Male, "Esiti di ricostruzione LCA sx", 60, 50, 0.25f, 0, 0),
        new("DEMO-005", "Sofia", "Romano", new(1940, 8, 30), Sex.Female, "Fragilità, rischio di caduta", 50, 15, 0.1f, 9.5, 0.01),
        new("DEMO-006", "Paolo", "Ricci", new(1966, 4, 17), Sex.Male, "Ictus ischemico, emiparesi sx", 45, 35, 0.4f, 0, 0),
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        if (await db.Patients.AnyAsync(ct))
        {
            return;
        }

        logger.LogWarning("Demo:Seed attivo: creazione di pazienti e rilevazioni FITTIZI.");
        var device = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "demo-seeder")], "demo"));
        var now = clock.GetUtcNow();
        var rng = new Random(20261011);

        foreach (var dp in Patients)
        {
            db.Patients.Add(new Patient
            {
                Id = Guid.CreateVersion7(), Code = dp.Code, FirstName = dp.First, LastName = dp.Last, BirthDate = dp.Birth, Sex = dp.Sex,
                Diagnosis = dp.Diagnosis, Notes = "Paziente fittizio generato per dimostrazione.", CreatedAt = now.AddDays(-90), UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);

        foreach (var dp in Patients)
        {
            var sessions = 4 + rng.Next(3);
            for (var s = 0; s < sessions; s++)
            {
                var progress = s / (float)Math.Max(1, sessions - 1);
                var started = now.AddDays(-84 + s * (80.0 / sessions) + rng.NextDouble()).AddHours(9 + rng.Next(8));
                await CreateSessionAsync(dp, progress, started, rng, device, ct);
            }
        }

        logger.LogInformation("Demo: creati {Patients} pazienti.", Patients.Length);
    }

    private async Task CreateSessionAsync(DemoPatient dp, float progress, DateTimeOffset started, Random rng, ClaimsPrincipal device, CancellationToken ct)
    {
        var session = new ClinicalSession
        {
            Id = ClinicalSession.NewId(), SubjectCode = dp.Code, OperatorId = "FT01", OperatorName = "Fisioterapista demo",
            Mode = CaptureMode.BodyLidar, StartedAt = started, EndedAt = started.AddSeconds(40), DeviceModel = "iPhone13,3",
            Notes = progress == 0 ? "Valutazione iniziale." : null,
        };

        const double duration = 40;
        const double poseFps = 15;
        var aggregator = new SessionMetricsAggregator(session.Mode);
        var streams = new Dictionary<SensorKind, List<SensorReading>>();

        void Emit(SensorReading r)
        {
            aggregator.Add(r);
            if (!streams.TryGetValue(r.Kind, out var list))
            {
                streams[r.Kind] = list = [];
            }

            list.Add(r);
        }

        // Squat ripetuti (periodo 4 s) con elevazione delle braccia: l'escursione massima migliora con la riabilitazione.
        var kneeMax = (dp.KneeStartDeg + dp.KneeGainDeg * progress) * MathF.PI / 180;
        var depth = 2.3f + (float)rng.NextDouble() * 0.6f;
        for (var i = 0; i < duration * poseFps; i++)
        {
            var t = i / poseFps;
            var phase = 0.5f * (1 - MathF.Cos(2 * MathF.PI * (float)t / 4f));
            var pose = SyntheticMotion.Pose(kneeMax * phase, 1.6f * phase + 0.1f, 0.3f + 0.8f * phase, dp.Asymmetry, depth, rng, 0.006f);
            var angles = JointAngleCalculator.Compute(pose);
            var values3d = new float[PoseLandmark.Count * 3];
            for (var j = 0; j < pose.Length; j++)
            {
                values3d[j * 3] = pose[j].X;
                values3d[j * 3 + 1] = pose[j].Y;
                values3d[j * 3 + 2] = pose[j].Z;
            }

            var angleValues = new float[angles.Length + 2];
            angles.CopyTo(angleValues, 0);
            angleValues[^2] = 0;
            angleValues[^1] = depth;
            Emit(new SensorReading(SensorKind.PoseLandmarks3D, t, values3d));
            Emit(new SensorReading(SensorKind.JointAngles, t, angleValues));
        }

        // IMU a 100 Hz: movimento lento dello squat + tremore del paziente (se presente) + rumore del sensore.
        var tremorG = dp.TremorG * (1 - 0.3 * progress);
        for (var i = 0; i < duration * 100; i++)
        {
            var t = i / 100.0;
            var slow = 0.05 * Math.Sin(2 * Math.PI * t / 4);
            var tremor = tremorG * Math.Sin(2 * Math.PI * (dp.TremorHz + 0.2 * Math.Sin(t / 7)) * t);
            float N() => (float)((rng.NextDouble() - 0.5) * 0.004);
            Emit(new SensorReading(SensorKind.Motion, t,
            [
                (float)(slow + tremor) + N(), (float)(0.6 * tremor) + N(), (float)(0.3 * slow) + N(),
                N(), N(), N(), 0, 0, 0, 1, 20, -5, -40, 0, -1, 0,
            ]));
        }

        var steps = rng.Next(10, 40);
        Emit(new SensorReading(SensorKind.Pedometer, duration, [steps, steps * 0.62f, 1.6f + (float)rng.NextDouble() * 0.4f, 0, 0]));
        for (var t = 0.0; t < duration; t += 0.5)
        {
            Emit(new SensorReading(SensorKind.AmbientLight, t, [600 + (float)rng.NextDouble() * 80, 4800]));
            Emit(new SensorReading(SensorKind.Barometer, t, [(float)(0.02 * Math.Sin(t)), 101.2f]));
        }

        var audio = new AudioFeatures(duration, -32 - rng.NextDouble() * 6, -9 - rng.NextDouble() * 4, 950 + rng.Next(400), 0.35 + rng.NextDouble() * 0.3, 0);
        session.Summary = aggregator.Build(duration, audio, dropped: 0);

        var analysis = new AnalysisUpload(
            Guid.NewGuid().ToString("N"), started.AddMinutes(5), "gemma4:e4b-mlx", "Italiano", "FT01", "(prompt demo)",
            $"**Sintesi** — Rilevazione dimostrativa. Flessione massima del ginocchio dx circa {kneeMax * 180 / MathF.PI:F0}°." +
            (dp.TremorHz > 0 ? $" Componente oscillatoria a ~{dp.TremorHz:F1} Hz compatibile con tremore." : string.Empty) +
            "\n\n*Testo generato per la demo, non è una valutazione clinica.*");

        await ingest.UpsertSessionAsync(new SessionUpload(
            session.Id, session.SubjectCode, session.OperatorId, session.OperatorName, session.Mode, session.StartedAt, session.EndedAt,
            session.Notes, session.DeviceModel, session.Summary,
            [new AudioUpload(AudioRecording.NewId(), started, duration, audio)],
            [analysis]), device, ct);

        var chunks = new List<ChunkUpload>();
        foreach (var (kind, readings) in streams)
        {
            var seq = 0;
            foreach (var block in readings.Chunk(kind == SensorKind.Motion ? 1000 : 300))
            {
                chunks.Add(new ChunkUpload(kind, seq++, block[0].Timestamp, block[^1].Timestamp, block.Length, SensorChunkCodec.Encode(block)));
            }
        }

        await ingest.InsertChunksAsync(session.Id, chunks, ct);
    }
}
