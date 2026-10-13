using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClinicalSensorFusion.Server.Services;

/// <summary>
/// L'archivio audio deve essere scrivibile e avere spazio: se si riempie, l'app non riesce più a
/// consegnare le registrazioni. Poco spazio = degradato (si interviene prima del guasto).
/// </summary>
public sealed class AudioStorageHealthCheck(AudioStorage storage) : IHealthCheck
{
    /// <summary>Sotto questa soglia lo stato è "degradato": bastano poche registrazioni lunghe a esaurirla.</summary>
    public const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(storage.Root);
            var probe = Path.Combine(storage.Root, ".health-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);

            var free = new DriveInfo(storage.Root).AvailableFreeSpace;
            var data = new Dictionary<string, object> { ["freeBytes"] = free, ["minFreeBytes"] = MinFreeBytes };
            return Task.FromResult(free < MinFreeBytes
                ? HealthCheckResult.Degraded("Spazio su disco quasi esaurito per l'archivio audio.", data: data)
                : HealthCheckResult.Healthy("Archivio audio scrivibile.", data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Archivio audio non scrivibile.", ex));
        }
    }
}
