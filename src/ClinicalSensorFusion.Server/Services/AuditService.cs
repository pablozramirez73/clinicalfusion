using System.Security.Claims;
using ClinicalSensorFusion.Server.Data;

namespace ClinicalSensorFusion.Server.Services;

/// <summary>Aggiunge una voce al registro di audit nella stessa unità di lavoro del DbContext.</summary>
public sealed class AuditService(ClinicalDbContext db, TimeProvider clock)
{
    public void Record(ClaimsPrincipal user, string action, string? entityId = null) =>
        db.AuditLog.Add(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = user.Identity?.Name ?? "anonimo",
            Action = action,
            EntityId = entityId,
        });
}
