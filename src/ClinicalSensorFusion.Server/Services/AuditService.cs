using System.Security.Claims;
using ClinicalSensorFusion.Server.Data;

namespace ClinicalSensorFusion.Server.Services;

/// <summary>
/// Aggiunge una voce al registro di audit nella stessa unità di lavoro del DbContext, con indirizzo e
/// user-agent della richiesta in corso (assenti per le operazioni interne, es. i dati demo).
/// </summary>
public sealed class AuditService(ClinicalDbContext db, TimeProvider clock, IHttpContextAccessor http)
{
    public const string Anonymous = "anonimo";

    public void Record(ClaimsPrincipal user, string action, string? entityId = null)
    {
        var context = http.HttpContext;
        db.AuditLog.Add(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = Truncate(user.Identity?.Name, 100) ?? Anonymous,
            Action = action,
            EntityId = Truncate(entityId, 64),
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(context?.Request.Headers.UserAgent.ToString(), 256),
        });
    }

    // I valori possono arrivare dal client (nome utente di un login fallito, user-agent): mai oltre la colonna.
    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
