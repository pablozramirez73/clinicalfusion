using System.Security.Claims;
using ClinicalSensorFusion.Core.Telemetry;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record LoginRequest(string UserName, string Password);

public sealed record CurrentUser(string UserName, string DisplayName);

/// <summary>Modi di accesso disponibili, per la pagina di login.</summary>
public sealed record AuthProviders(bool Password, bool Google);

/// <summary>Risposta di <c>POST /auth/token</c> (stessi nomi di OAuth 2.0).</summary>
public sealed record TokenResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_type")] string TokenType,
    [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    /// <summary>Cookie temporaneo con l'esito dell'accesso a Google, prima di decidere se la persona può entrare.</summary>
    public const string ExternalScheme = "External";

    public const string EmailVerifiedClaim = "email_verified";

    public static RouteGroupBuilder MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Autenticazione");
        g.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
        g.MapPost("/token", TokenAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit)
            .WithSummary("Token JWT per usare le API senza browser (Swagger, script).");
        g.MapPost("/logout", (Delegate)LogoutAsync).RequireAuthorization(Policies.Clinician);
        g.MapGet("/me", Me).RequireAuthorization(Policies.Clinician);
        g.MapGet("/providers", (IOptions<GoogleSignInOptions> google) => TypedResults.Ok(new AuthProviders(true, google.Value.IsConfigured)))
            .AllowAnonymous();
        g.MapGet("/google", StartGoogle).AllowAnonymous().RequireRateLimiting(LoginRateLimit)
            .WithSummary("Accesso con Google: reindirizza a Google e poi torna alla dashboard.");
        g.MapGet("/google/complete", CompleteGoogleAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit)
            .ExcludeFromDescription();
        return api;
    }

    private static IResult StartGoogle(string? returnUrl, IOptions<GoogleSignInOptions> google)
    {
        if (!google.Value.IsConfigured)
        {
            return TypedResults.NotFound();
        }

        var complete = "/api/v1/auth/google/complete?returnUrl=" + Uri.EscapeDataString(LocalPath(returnUrl));
        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = complete }, [GoogleDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// Ritorno da Google. Google ha confermato l'identità; qui si decide se è una persona autorizzata: indirizzo
    /// verificato e associato a un utente della dashboard. Solo allora si apre la sessione, identica a quella
    /// del login con password.
    /// </summary>
    private static async Task<IResult> CompleteGoogleAsync(
        string? returnUrl, HttpContext http, DashboardUserService users, AuditService audit, Data.ClinicalDbContext db)
    {
        var external = await http.AuthenticateAsync(ExternalScheme);
        if (!external.Succeeded)
        {
            return TypedResults.Redirect("/login?error=google");
        }

        await http.SignOutAsync(ExternalScheme);
        var email = external.Principal.FindFirstValue(ClaimTypes.Email);
        var verified = string.Equals(external.Principal.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);
        var user = users.FindByGoogle(email, verified);
        if (user is null)
        {
            // L'indirizzo tentato va nel registro: serve a capire chi ha provato a entrare.
            audit.Record(http.User, "auth.login.failed", "google:" + email);
            await db.SaveChangesAsync();
            CountLogin("google", success: false);
            return TypedResults.Redirect("/login?error=google-denied");
        }

        var principal = Principal(user);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        audit.Record(principal, "auth.login", "google");
        await db.SaveChangesAsync();
        CountLogin("google", success: true);
        return TypedResults.Redirect(LocalPath(returnUrl));
    }

    /// <summary>Solo percorsi di questo sito: niente reindirizzamenti verso altri domini (open redirect).</summary>
    internal static string LocalPath(string? returnUrl) =>
        returnUrl is ['/', not '/' and not '\\', ..] or "/" ? returnUrl : "/";

    private static ClaimsPrincipal Principal(DashboardUser user) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim("display_name", user.DisplayName ?? user.UserName),
            new Claim(ClaimTypes.Role, Roles.Clinician),
        ],
        CookieAuthenticationDefaults.AuthenticationScheme));

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

        var principal = Principal(user);
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
