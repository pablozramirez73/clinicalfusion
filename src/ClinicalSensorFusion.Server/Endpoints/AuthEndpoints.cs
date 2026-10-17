using ClinicalSensorFusion.Core.Localization;
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

public sealed record RegisterRequest(string Email, string? DisplayName, string Password);

public sealed record CurrentUser(string UserName, string DisplayName, bool IsAdmin = false);

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
        g.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit)
            .WithSummary("Registrazione con email e password: crea una richiesta da approvare.");
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
    /// Ritorno da Google. Google ha confermato l'identità; qui si decide cosa può fare: entrare (utente della
    /// configurazione o account approvato), registrare una richiesta da approvare, o niente. La sessione aperta
    /// è la stessa del login con password.
    /// </summary>
    private static async Task<IResult> CompleteGoogleAsync(
        string? returnUrl, HttpContext http, AccountService accounts, AuditService audit, Data.ClinicalDbContext db)
    {
        var external = await http.AuthenticateAsync(ExternalScheme);
        if (!external.Succeeded)
        {
            return TypedResults.Redirect("/login?error=google");
        }

        await http.SignOutAsync(ExternalScheme);
        var google = external.Principal;
        var email = google.FindFirstValue(ClaimTypes.Email);
        var verified = string.Equals(google.FindFirstValue(EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase);
        var result = await accounts.SignInWithGoogleAsync(
            email, verified, google.FindFirstValue(ClaimTypes.NameIdentifier), google.FindFirstValue(ClaimTypes.Name), http.User, http.RequestAborted);

        switch (result.Outcome)
        {
            case GoogleSignInOutcome.SignedIn:
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal!);
                audit.Record(result.Principal!, "auth.login", "google");
                await db.SaveChangesAsync();
                CountLogin("google", success: true);
                return TypedResults.Redirect(LocalPath(returnUrl));
            case GoogleSignInOutcome.Requested:
                return TypedResults.Redirect("/login?status=requested");
            case GoogleSignInOutcome.Pending:
                return TypedResults.Redirect("/login?status=pending");
            default:
                // L'indirizzo tentato va nel registro: serve a capire chi ha provato a entrare.
                audit.Record(http.User, "auth.login.failed", "google:" + email);
                await db.SaveChangesAsync();
                CountLogin("google", success: false);
                return TypedResults.Redirect("/login?error=google-denied");
        }
    }

    /// <summary>Solo percorsi di questo sito: niente reindirizzamenti verso altri domini (open redirect).</summary>
    internal static string LocalPath(string? returnUrl) =>
        returnUrl is ['/', not '/' and not '\\', ..] or "/" ? returnUrl : "/";


    /// <summary>Codice del problema restituito a chi ha la richiesta di accesso ancora in attesa.</summary>
    public const string PendingProblem = "account-pending";

    private static async Task<Results<Ok<CurrentUser>, UnauthorizedHttpResult, ProblemHttpResult>> LoginAsync(
        LoginRequest request, DashboardUserService users, AccountService accounts, AuditService audit, Data.ClinicalDbContext db, HttpContext http)
    {
        ClaimsPrincipal? principal = null;
        CurrentUser? current = null;
        if (users.Validate(request.UserName ?? "", request.Password ?? "") is { } user)
        {
            // Gli utenti della configurazione sono gli amministratori: approvano le richieste di accesso.
            principal = AccountService.Principal(user.UserName, user.DisplayName, isAdmin: true);
            current = new CurrentUser(user.UserName, user.DisplayName ?? user.UserName, IsAdmin: true);
        }
        else
        {
            // Account registrati con email e password: entrano solo dopo l'approvazione.
            var result = await accounts.SignInWithPasswordAsync(request.UserName, request.Password, http.RequestAborted);
            if (result.Outcome == GoogleSignInOutcome.Pending)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    type: PendingProblem,
                    detail: Loc.T("La richiesta di accesso è in attesa di approvazione.", Ui.Culture));
            }

            if (result.Principal is { } p)
            {
                principal = p;
                current = new CurrentUser(p.Identity!.Name!, p.FindFirstValue(AccountService.DisplayNameClaim) ?? p.Identity.Name!, p.IsInRole(Roles.Admin));
            }
        }

        if (principal is null)
        {
            // Il nome tentato va nel registro (mai la password): serve a riconoscere i tentativi di intrusione.
            audit.Record(http.User, "auth.login.failed", request.UserName);
            await db.SaveChangesAsync();
            CountLogin("cookie", success: false);
            return TypedResults.Unauthorized();
        }

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        audit.Record(principal, "auth.login");
        await db.SaveChangesAsync();
        CountLogin("cookie", success: true);
        return TypedResults.Ok(current!);
    }

    /// <summary>
    /// Registrazione con email e password: crea una richiesta che un amministratore approva dalla pagina Accessi.
    /// Risponde sempre allo stesso modo se l'indirizzo esiste già.
    /// </summary>
    private static async Task<Results<Accepted, ValidationProblem>> RegisterAsync(
        RegisterRequest request, AccountService accounts, HttpContext http)
    {
        var outcome = await accounts.RegisterAsync(request.Email, request.DisplayName, request.Password, http.User, http.RequestAborted);
        return outcome switch
        {
            RegistrationOutcome.InvalidEmail => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = [Loc.T("Indirizzo email non valido.", Ui.Culture)] }),
            RegistrationOutcome.WeakPassword => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = [string.Format(Ui.Culture, Loc.T("La password deve avere almeno {0} caratteri.", Ui.Culture), AccountService.MinPasswordLength)],
            }),
            _ => TypedResults.Accepted((string?)null),
        };
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
        TypedResults.Ok(new CurrentUser(
            user.Identity!.Name!, user.FindFirstValue(JwtTokenService.DisplayNameClaim) ?? user.Identity.Name!, user.IsInRole(Roles.Admin)));
}
