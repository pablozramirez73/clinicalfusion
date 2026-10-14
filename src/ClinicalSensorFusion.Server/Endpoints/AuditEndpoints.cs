using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record AuditItem(long Id, DateTimeOffset At, string Actor, string Action, string? EntityId, string? IpAddress, string? UserAgent);

/// <summary>Consultazione del registro di audit (sola lettura: le voci non si modificano né si cancellano da API).</summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAudit(this RouteGroupBuilder api)
    {
        api.MapGet("/audit", ListAsync).WithTags("Registro di audit").RequireAuthorization(Policies.Clinician);
        return api;
    }

    /// <param name="action">Azione esatta (<c>auth.login.failed</c>) o prefisso con punto finale (<c>auth.</c>).</param>
    private static async Task<Ok<PagedResult<AuditItem>>> ListAsync(
        ClinicalDbContext db, AuditService audit, HttpContext http,
        string? actor, string? action, string? entityId, DateTimeOffset? from, DateTimeOffset? to,
        int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(actor))
        {
            query = query.Where(a => a.Actor == actor);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            query = action.EndsWith('.') ? query.Where(a => a.Action.StartsWith(action)) : query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        if (from is { } f)
        {
            query = query.Where(a => a.At >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.At < t);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new AuditItem(a.Id, a.At, a.Actor, a.Action, a.EntityId, a.IpAddress, a.UserAgent))
            .ToListAsync(ct);

        // Anche la consultazione del registro è un accesso da tracciare.
        audit.Record(http.User, "audit.read");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new PagedResult<AuditItem>(items, total, page, pageSize));
    }
}
