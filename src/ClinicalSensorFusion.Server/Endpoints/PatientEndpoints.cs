using System.Net.Mail;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record PatientListItem(
    Guid Id, string Code, string? FirstName, string? LastName, DateOnly? BirthDate, Sex Sex,
    bool IsPlaceholder, int SessionCount, DateTimeOffset? LastSessionAt);

public sealed record PatientDetail(
    Guid Id, string Code, string? FirstName, string? LastName, DateOnly? BirthDate, Sex Sex,
    string? FiscalCode, string? Phone, string? Email, string? Address, string? Diagnosis, string? Notes,
    bool IsPlaceholder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, uint Version, int SessionCount);

/// <summary>Dati modificabili dell'anagrafica. <see cref="Version"/> è obbligatoria in modifica (concorrenza ottimistica).</summary>
public sealed record PatientInput(
    string Code, string? FirstName, string? LastName, DateOnly? BirthDate, Sex Sex,
    string? FiscalCode, string? Phone, string? Email, string? Address, string? Diagnosis, string? Notes, uint? Version);

public static class PatientEndpoints
{
    public static RouteGroupBuilder MapPatients(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/patients").WithTags("Anagrafica pazienti").RequireAuthorization(Policies.Clinician);

        g.MapGet("/", ListAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPost("/", CreateAsync);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        return api;
    }

    private static async Task<Ok<PagedResult<PatientListItem>>> ListAsync(
        ClinicalDbContext db, string? search, bool? placeholdersOnly, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.Patients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(p =>
                EF.Functions.ILike(p.Code, pattern) ||
                EF.Functions.ILike(p.LastName ?? "", pattern) ||
                EF.Functions.ILike(p.FirstName ?? "", pattern) ||
                EF.Functions.ILike(p.FiscalCode ?? "", pattern));
        }

        if (placeholdersOnly == true)
        {
            query = query.Where(p => p.IsPlaceholder);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(p => p.LastName ?? p.Code).ThenBy(p => p.FirstName)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new PatientListItem(
                p.Id, p.Code, p.FirstName, p.LastName, p.BirthDate, p.Sex, p.IsPlaceholder,
                p.Sessions.Count, p.Sessions.Max(s => (DateTimeOffset?)s.StartedAt)))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResult<PatientListItem>(items, total, page, pageSize));
    }

    private static async Task<Results<Ok<PatientDetail>, NotFound>> GetAsync(
        Guid id, ClinicalDbContext db, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var p = await db.Patients.AsNoTracking().Where(x => x.Id == id).Select(x => new { x, Count = x.Sessions.Count }).FirstOrDefaultAsync(ct);
        if (p is null)
        {
            return TypedResults.NotFound();
        }

        // Anche la sola lettura dell'anagrafica è tracciata.
        audit.Record(http.User, "patient.read", id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDetail(p.x, p.Count));
    }

    private static async Task<Results<Created<PatientDetail>, ValidationProblem, Conflict<string>>> CreateAsync(
        PatientInput input, ClinicalDbContext db, AuditService audit, HttpContext http, TimeProvider clock, CancellationToken ct)
    {
        if (Validate(input) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var code = Patient.NormalizeCode(input.Code);
        if (await db.Patients.AnyAsync(p => p.Code == code, ct))
        {
            return TypedResults.Conflict($"Esiste già un paziente con codice {code}.");
        }

        var now = clock.GetUtcNow();
        var patient = new Patient { Id = Guid.CreateVersion7(), Code = code, CreatedAt = now };
        Apply(patient, input, now);
        db.Patients.Add(patient);
        audit.Record(http.User, "patient.create", patient.Id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/patients/{patient.Id}", ToDetail(patient, 0));
    }

    private static async Task<Results<Ok<PatientDetail>, NotFound, ValidationProblem, Conflict<string>>> UpdateAsync(
        Guid id, PatientInput input, ClinicalDbContext db, AuditService audit, HttpContext http, TimeProvider clock, CancellationToken ct)
    {
        if (Validate(input) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (input.Version is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["version"] = ["Versione mancante."] });
        }

        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
        {
            return TypedResults.NotFound();
        }

        var code = Patient.NormalizeCode(input.Code);
        if (code != patient.Code)
        {
            // Il codice è il legame con i dati dell'app: cambiarlo è ammesso solo se non collide.
            if (await db.Patients.AnyAsync(p => p.Code == code && p.Id != id, ct))
            {
                return TypedResults.Conflict($"Esiste già un paziente con codice {code}.");
            }

            patient.Code = code;
        }

        // Concorrenza ottimistica: EF confronta xmin con la versione letta dal client.
        db.Entry(patient).Property(p => p.Version).OriginalValue = input.Version.Value;
        Apply(patient, input, clock.GetUtcNow());
        audit.Record(http.User, "patient.update", id.ToString());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Conflict("L'anagrafica è stata modificata da un altro utente: ricaricare e riprovare.");
        }

        var count = await db.Sessions.CountAsync(s => s.PatientId == id, ct);
        return TypedResults.Ok(ToDetail(patient, count));
    }

    /// <summary>Cancellazione definitiva (diritto all'oblio): anagrafica, rilevazioni, blocchi e file audio.</summary>
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id, ClinicalDbContext db, AudioStorage storage, AuditService audit, HttpContext http, CancellationToken ct)
    {
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
        {
            return TypedResults.NotFound();
        }

        var sessionIds = await db.Sessions.Where(s => s.PatientId == id).Select(s => s.Id).ToListAsync(ct);
        db.Patients.Remove(patient);
        audit.Record(http.User, "patient.delete", id.ToString());
        await db.SaveChangesAsync(ct);
        foreach (var sid in sessionIds)
        {
            storage.DeleteSession(sid);
        }

        return TypedResults.NoContent();
    }

    private static void Apply(Patient p, PatientInput i, DateTimeOffset now)
    {
        p.FirstName = Clean(i.FirstName);
        p.LastName = Clean(i.LastName);
        p.BirthDate = i.BirthDate;
        p.Sex = i.Sex;
        p.FiscalCode = Clean(i.FiscalCode)?.ToUpperInvariant();
        p.Phone = Clean(i.Phone);
        p.Email = Clean(i.Email);
        p.Address = Clean(i.Address);
        p.Diagnosis = Clean(i.Diagnosis);
        p.Notes = Clean(i.Notes);
        p.IsPlaceholder = p.FirstName is null && p.LastName is null;
        p.UpdatedAt = now;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static Dictionary<string, string[]>? Validate(PatientInput i)
    {
        var errors = new Dictionary<string, string[]>();
        var code = i.Code?.Trim() ?? "";
        if (code.Length is 0 or > 64 || code.Any(char.IsControl))
        {
            errors["code"] = ["Codice obbligatorio, massimo 64 caratteri."];
        }

        if (Clean(i.FiscalCode) is { } cf && (cf.Length != 16 || !cf.All(char.IsAsciiLetterOrDigit)))
        {
            errors["fiscalCode"] = ["Il codice fiscale deve avere 16 caratteri alfanumerici."];
        }

        if (Clean(i.Email) is { } email && !MailAddress.TryCreate(email, out _))
        {
            errors["email"] = ["Indirizzo email non valido."];
        }

        if (i.BirthDate is { } birth && (birth > DateOnly.FromDateTime(DateTime.UtcNow) || birth.Year < 1900))
        {
            errors["birthDate"] = ["Data di nascita non plausibile."];
        }

        if (i.FirstName?.Length > 100 || i.LastName?.Length > 100)
        {
            errors["name"] = ["Nome e cognome: massimo 100 caratteri."];
        }

        return errors.Count == 0 ? null : errors;
    }

    private static PatientDetail ToDetail(Patient p, int sessions) => new(
        p.Id, p.Code, p.FirstName, p.LastName, p.BirthDate, p.Sex, p.FiscalCode, p.Phone, p.Email, p.Address,
        p.Diagnosis, p.Notes, p.IsPlaceholder, p.CreatedAt, p.UpdatedAt, p.Version, sessions);
}
