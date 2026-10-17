using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ClinicalSensorFusion.Server.Endpoints;

/// <summary>Decisione dell'amministratore su una richiesta o un account.</summary>
/// <param name="IsAdmin">Solo con l'approvazione: rende l'account anche amministratore.</param>
public sealed record AccountDecision(bool? IsAdmin = null);

/// <summary>Richieste di accesso e account della dashboard registrati con Google: solo per gli amministratori.</summary>
public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/accounts").WithTags("Accessi alla dashboard").RequireAuthorization(Policies.Admin);
        g.MapGet("/", async (AccountService accounts, CancellationToken ct) => TypedResults.Ok(await accounts.ListAsync(ct)));
        g.MapPost("/{id:guid}/approve", (Guid id, AccountDecision? decision, AccountService accounts, HttpContext http) =>
            DecideAsync(id, AccountStatus.Approved, decision?.IsAdmin, accounts, http));
        g.MapPost("/{id:guid}/reject", (Guid id, AccountService accounts, HttpContext http) =>
            DecideAsync(id, AccountStatus.Rejected, null, accounts, http));
        g.MapPost("/{id:guid}/disable", (Guid id, AccountService accounts, HttpContext http) =>
            DecideAsync(id, AccountStatus.Disabled, null, accounts, http));
        return api;
    }

    private static async Task<Results<Ok<AccountItem>, NotFound>> DecideAsync(
        Guid id, AccountStatus status, bool? isAdmin, AccountService accounts, HttpContext http) =>
        await accounts.DecideAsync(id, status, isAdmin, http.User, http.RequestAborted) is { } item
            ? TypedResults.Ok(item)
            : TypedResults.NotFound();
}
