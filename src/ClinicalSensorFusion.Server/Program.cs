using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Demo;
using ClinicalSensorFusion.Server.Endpoints;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

// Comandi di amministrazione (nessun segreto in chiaro nella configurazione):
//   dotnet run -- hash-password <password>     → hash per Dashboard:Users
//   dotnet run -- new-device-key <nome>        → chiave per l'app + hash per Ingest:Devices
if (args is ["hash-password", var password])
{
    Console.WriteLine(DashboardUserService.HashPassword(password));
    return;
}

if (args is ["new-device-key", var deviceName])
{
    var key = ApiKeyAuthenticationHandler.Generate();
    Console.WriteLine($"Chiave da inserire nell'app (mostrata una sola volta): {key}");
    Console.WriteLine($$"""Configurazione server: { "Name": "{{deviceName}}", "KeySha256": "{{ApiKeyAuthenticationHandler.Hash(key)}}" }""");
    return;
}

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddSingleton(TimeProvider.System);
services.AddDbContext<ClinicalDbContext>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("Clinical")
        ?? throw new InvalidOperationException("ConnectionStrings:Clinical mancante.")));

services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // I riepiloghi possono contenere NaN (es. distanza del pedometro non disponibile).
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
services.AddProblemDetails();
services.AddOpenApi();
services.AddHealthChecks().AddDbContextCheck<ClinicalDbContext>("database");

services.Configure<IngestOptions>(builder.Configuration.GetSection("Ingest"));
services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
services.Configure<AudioStorageOptions>(builder.Configuration.GetSection("AudioStorage"));
services.AddSingleton<DashboardUserService>();
services.AddSingleton<AudioStorage>();
services.AddScoped<AuditService>();
services.AddScoped<IngestService>();
services.AddScoped<SessionAnalytics>();
services.AddScoped<DemoDataSeeder>();

// Due mondi separati: i dispositivi (chiave API, solo ingestione) e i clinici (cookie, solo dashboard).
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "csf_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict; // protezione CSRF per una SPA sullo stesso dominio
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        // API JSON: 401/403 invece del redirect a una pagina di login.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Ingest, p => p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireRole(Roles.Device))
    .AddPolicy(Policies.Clinician, p => p.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireRole(Roles.Clinician));

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Contro i tentativi di indovinare le password: 5 login al minuto per indirizzo IP.
    o.AddPolicy(AuthEndpoints.LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

services.Configure<ForwardedHeadersOptions>(o =>
{
    // Dietro un reverse proxy (Cloudflare Tunnel, nginx): IP e schema originali per rate limit e cookie Secure.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (!ctx.Request.Path.StartsWithSegments("/scalar"))
    {
        h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                                  "media-src 'self' blob:; connect-src 'self'; worker-src 'self' blob:; frame-ancestors 'none'; base-uri 'self'";
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1");
api.MapAuth().MapPatients().MapSessions().MapDashboard().MapIngest();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapHealthChecks("/health").AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// SPA React: qualunque altra rotta torna a index.html (routing lato client).
app.MapFallbackToFile("index.html");

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClinicalDbContext>();
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        await db.Database.MigrateAsync();
    }

    if (app.Configuration.GetValue("Demo:Seed", false))
    {
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(CancellationToken.None);
    }
}

await app.RunAsync();

public partial class Program;
