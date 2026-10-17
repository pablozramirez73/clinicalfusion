using System.Security.Claims;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ClinicalSensorFusion.Server.Security;

/// <summary>Esito di un accesso con Google.</summary>
public enum GoogleSignInOutcome
{
    /// <summary>Persona autorizzata: sessione aperta.</summary>
    SignedIn,

    /// <summary>Primo accesso: registrata una richiesta, da approvare.</summary>
    Requested,

    /// <summary>Richiesta già registrata e non ancora decisa.</summary>
    Pending,

    /// <summary>Indirizzo non verificato, richiesta rifiutata o accesso tolto.</summary>
    Denied,
}

public sealed record GoogleSignInResult(GoogleSignInOutcome Outcome, ClaimsPrincipal? Principal = null);

/// <summary>Una richiesta o un account, come lo vede l'amministratore.</summary>
public sealed record AccountItem(
    Guid Id, string Email, string? DisplayName, AccountStatus Status, bool IsAdmin,
    DateTimeOffset RequestedAt, DateTimeOffset? DecidedAt, string? DecidedBy, DateTimeOffset? LastLoginAt);

/// <summary>
/// Registrazione con approvazione. Chi entra con Google per la prima volta crea una richiesta in attesa, che non
/// vede nessun dato; un amministratore (gli utenti della configurazione, o un account approvato e reso
/// amministratore) la approva o la rifiuta. Le decisioni valgono subito anche per le sessioni già aperte.
/// </summary>
public sealed class AccountService(ClinicalDbContext db, DashboardUserService users, AuditService audit, TimeProvider clock)
{
    public const string AccountIdClaim = "account_id";
    public const string DisplayNameClaim = "display_name";

    /// <summary>Sessione della dashboard, uguale per il login con password e per quello con Google.</summary>
    public static ClaimsPrincipal Principal(string userName, string? displayName, bool isAdmin, Guid? accountId = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.Name, userName),
            new(DisplayNameClaim, displayName ?? userName),
            new(ClaimTypes.Role, Roles.Clinician),
        ];
        if (isAdmin)
        {
            claims.Add(new(ClaimTypes.Role, Roles.Admin));
        }

        if (accountId is { } id)
        {
            claims.Add(new(AccountIdClaim, id.ToString()));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public async Task<GoogleSignInResult> SignInWithGoogleAsync(
        string? email, bool emailVerified, string? subject, string? name, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        // Senza indirizzo verificato da Google non si sa chi è: niente sessione e niente richiesta.
        if (!emailVerified || string.IsNullOrWhiteSpace(email))
        {
            return new(GoogleSignInOutcome.Denied);
        }

        var normalized = email.Trim().ToLowerInvariant();

        // Utenti della configurazione con l'indirizzo Google associato: amministratori, entrano subito.
        if (users.FindByGoogle(normalized, emailVerified: true) is { } configured)
        {
            return new(GoogleSignInOutcome.SignedIn, Principal(configured.UserName, configured.DisplayName, isAdmin: true));
        }

        var account = subject is null ? null : await db.Accounts.FirstOrDefaultAsync(a => a.GoogleSubject == subject, ct);
        account ??= await db.Accounts.FirstOrDefaultAsync(a => a.Email == normalized, ct);
        var now = clock.GetUtcNow();

        if (account is null)
        {
            account = new DashboardAccount
            {
                Id = Guid.CreateVersion7(),
                Email = normalized,
                GoogleSubject = subject,
                DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                Status = AccountStatus.Pending,
                RequestedAt = now,
            };
            db.Accounts.Add(account);
            audit.Record(actor, "account.request", normalized);
            await db.SaveChangesAsync(ct);
            return new(GoogleSignInOutcome.Requested);
        }

        switch (account.Status)
        {
            case AccountStatus.Pending:
                return new(GoogleSignInOutcome.Pending);
            case AccountStatus.Approved:
                account.LastLoginAt = now;
                account.GoogleSubject ??= subject;
                account.Email = normalized;
                await db.SaveChangesAsync(ct);
                return new(GoogleSignInOutcome.SignedIn, Principal(account.Email, account.DisplayName, account.IsAdmin, account.Id));
            default:
                return new(GoogleSignInOutcome.Denied);
        }
    }

    /// <summary>Vero se la sessione di questo account vale ancora (controllato a ogni richiesta).</summary>
    public Task<bool> IsActiveAsync(Guid accountId, CancellationToken ct = default) =>
        db.Accounts.AnyAsync(a => a.Id == accountId && a.Status == AccountStatus.Approved, ct);

    /// <summary>Prima le richieste in attesa, poi gli altri dal più recente.</summary>
    public async Task<IReadOnlyList<AccountItem>> ListAsync(CancellationToken ct = default) =>
        (await db.Accounts.AsNoTracking().ToListAsync(ct))
            .OrderBy(a => a.Status == AccountStatus.Pending ? 0 : 1)
            .ThenByDescending(a => a.RequestedAt)
            .Select(ToItem)
            .ToList();

    /// <summary>Approva, rifiuta, toglie o restituisce l'accesso; null se l'account non esiste.</summary>
    public async Task<AccountItem?> DecideAsync(Guid id, AccountStatus status, bool? isAdmin, ClaimsPrincipal admin, CancellationToken ct = default)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return null;
        }

        account.Status = status;
        account.IsAdmin = status == AccountStatus.Approved && (isAdmin ?? account.IsAdmin);
        account.DecidedAt = clock.GetUtcNow();
        account.DecidedBy = admin.Identity?.Name;
        audit.Record(admin, "account." + status.ToString().ToLowerInvariant(), account.Email);
        await db.SaveChangesAsync(ct);
        return ToItem(account);
    }

    private static AccountItem ToItem(DashboardAccount a) =>
        new(a.Id, a.Email, a.DisplayName, a.Status, a.IsAdmin, a.RequestedAt, a.DecidedAt, a.DecidedBy, a.LastLoginAt);
}
