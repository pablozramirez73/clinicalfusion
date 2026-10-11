using System.Security.Claims;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ClinicalSensorFusion.Server.Endpoints;

public sealed record LoginRequest(string UserName, string Password);

public sealed record CurrentUser(string UserName, string DisplayName);

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";

    public static RouteGroupBuilder MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Autenticazione");
        g.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting(LoginRateLimit);
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
        return TypedResults.Ok(new CurrentUser(user.UserName, user.DisplayName ?? user.UserName));
    }

    private static async Task<NoContent> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.NoContent();
    }

    private static Ok<CurrentUser> Me(ClaimsPrincipal user) =>
        TypedResults.Ok(new CurrentUser(user.Identity!.Name!, user.FindFirstValue("display_name") ?? user.Identity.Name!));
}
