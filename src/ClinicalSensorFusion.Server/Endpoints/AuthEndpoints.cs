using System.Security.Claims;
using ClinicalSensorFusion.Core.Telemetry;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record LoginRequest(string UserName, string Password);

public sealed record CurrentUser(string UserName, string DisplayName);

/// <summary>Risposta di <c>POST /auth/token</c> (stessi nomi di OAuth 2.0).</summary>
public sealed record TokenResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_type")] string TokenType,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static RouteGroupBuilder MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Autenticazione");
        g.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
        g.MapPost("/token", TokenAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit)
            .WithSummary("Token JWT per usare le API senza browser (Swagger, script).");
        g.MapPost("/logout", (Delegate)LogoutAsync).RequireAuthorization(Policies.Clinician);
        g.MapGet("/me", Me).RequireAuthorization(Policies.Clinician);
        return api;
    }

    private static async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult>> LoginAsync(
        LoginRequest request, DashboardUserService users, AuditService audit, Data.ClinicalDbContext db, HttpContext http)
    {
        var user = users.Validate(request.UserName ?? "", request.Password ?? "");
        if (user is null)
        {
            // Il nome tentato va nel registro (mai la password): serve a riconoscere i tentativi di intrusione.
            audit.Record(http.User, "auth.login.failed", request.UserName);
            await db.SaveChangesAsync();
            CountLogin("cookie", success: false);
            return TypedResults.Unauthorized();
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim("display_name", user.DisplayName ?? user.UserName),
                new Claim(ClaimTypes.Role, Roles.Clinician),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        audit.Record(principal, "auth.login");
        await db.SaveChangesAsync();
        CountLogin("cookie", success: true);
        return TypedResults.Ok(new CurrentUser(user.UserName, user.DisplayName ?? user.UserName));
    }

    /// <summary>Stesse credenziali, stesso limite di tentativi e stesso audit del login della dashboard.</summary>
    private static async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>> TokenAsync(
        LoginRequest request, DashboardUserService users, JwtTokenService tokens, AuditService audit, Data.ClinicalDbContext db, HttpContext http)
    {
        var user = users.Validate(request.UserName ?? "", request.Password ?? "");
        if (user is null)
        {
            audit.Record(http.User, "auth.login.failed", request.UserName);
            await db.SaveChangesAsync();
            CountLogin("token", success: false);
            return TypedResults.Unauthorized();
        }

        var token = tokens.Create(user);
        audit.Record(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.UserName)], "token")), "auth.token");
        await db.SaveChangesAsync();
        CountLogin("token", success: true);
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new TokenResponse(token.Token, "Bearer", token.ExpiresInSeconds));
    }

    private static void CountLogin(string method, bool success) => CsfTelemetry.Server.Logins.Add(
        1,
        CsfTelemetry.Tag("method", method),
        CsfTelemetry.Tag("outcome", success ? CsfTelemetry.Outcomes.Success : CsfTelemetry.Outcomes.Error));

    private static async Task<NoContent> LogoutAsync(HttpContext http, AuditService audit, Data.ClinicalDbContext db)
    {
        audit.Record(http.User, "auth.logout");
        await db.SaveChangesAsync();
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    private static Ok<CurrentUser> Me(ClaimsPrincipal user) =>
        TypedResults.Ok(new CurrentUser(user.Identity!.Name!, user.FindFirstValue(JwtTokenService.DisplayNameClaim) ?? user.Identity.Name!));
}
