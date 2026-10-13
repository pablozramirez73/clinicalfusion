using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClinicalSensorFusion.Core.Sync;
using ClinicalSensorFusion.Core.Telemetry;
using ClinicalSensorFusion.Server;
using ClinicalSensorFusion.Server.Data;
using ClinicalSensorFusion.Server.Demo;
using ClinicalSensorFusion.Server.Endpoints;
using ClinicalSensorFusion.Server.Fhir;
using ClinicalSensorFusion.Server.Security;
using ClinicalSensorFusion.Server.Services;
using ClinicalSensorFusion.Server.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
builder.AddCsfTelemetry();
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
services.AddCsfOpenApi();
services.AddHealthChecks()
    .AddDbContextCheck<ClinicalDbContext>("database", tags: [SystemEndpoints.ReadyTag])
    .AddCheck<AudioStorageHealthCheck>("audio-storage", tags: [SystemEndpoints.ReadyTag]);

services.Configure<IngestOptions>(builder.Configuration.GetSection("Ingest"));
services.Configure<DashboardOptions>(builder.Configuration.GetSection("Dashboard"));
services.Configure<AudioStorageOptions>(builder.Configuration.GetSection("AudioStorage"));
services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
services.Configure<FhirOptions>(builder.Configuration.GetSection("Fhir"));
services.AddSingleton<JwtTokenService>();
services.AddHttpContextAccessor();
services.AddSingleton<DashboardUserService>();
services.AddSingleton<AudioStorage>();
services.AddScoped<AuditService>();
services.AddScoped<IngestService>();
services.AddScoped<SessionAnalytics>();
services.AddScoped<DemoDataSeeder>();

// Due mondi separati: i dispositivi (chiave API, solo ingestione) e i clinici (cookie della dashboard,
// oppure token JWT per chi usa le API senza browser: Swagger, script).
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
    .AddJwtBearer(o => o.MapInboundClaims = false)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtTokenService>((o, tokens) => o.TokenValidationParameters = tokens.ValidationParameters);

services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Ingest, p => p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireRole(Roles.Device))
    .AddPolicy(Policies.Clinician, p => p
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme)
        .RequireRole(Roles.Clinician));

// Limiti letti dalle opzioni al primo uso (non qui): così valgono anche le configurazioni aggiunte dopo, es. nei test.
services.Configure<RateLimitOptions>(builder.Configuration.GetSection("RateLimits"));
services.AddRateLimiter(_ => { });
services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((o, configured) =>
{
    var limits = configured.Value;
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        CsfTelemetry.Server.RateLimitRejections.Add(1);
        return ValueTask.CompletedTask;
    };

    // Tetto generale per indirizzo IP: vale per ogni rotta e si somma ai limiti specifici qui sotto.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => PerMinute(ClientIp(http), limits.GlobalPerMinute));
    // Contro i tentativi di indovinare le password.
    o.AddPolicy(AuthEndpoints.LoginRateLimit, http => PerMinute(ClientIp(http), limits.LoginPerMinute));
    // Sonde di salute anonime che eseguono i controlli (database, archivio audio): un tetto basso toglie
    // l'amplificazione. Si somma al limite globale, quindi il tetto effettivo è il più stretto dei due.
    o.AddPolicy(SystemEndpoints.HealthRateLimit, http => PerMinute(ClientIp(http), limits.HealthPerMinute));
    // Ingestione: per dispositivo, così un telefono non consuma il margine degli altri dietro lo stesso NAT.
    o.AddPolicy(IngestEndpoints.RateLimit, http => PerMinute(DeviceOrIp(http), limits.IngestPerMinute));
    // I file audio sono grandi (centinaia di MB): pochi alla volta per dispositivo, il resto riceve 429.
    o.AddPolicy(IngestEndpoints.AudioRateLimit, http => RateLimitPartition.GetConcurrencyLimiter(
        DeviceOrIp(http),
        _ => new ConcurrencyLimiterOptions { PermitLimit = limits.ConcurrentAudioUploads, QueueLimit = 0 }));

    static RateLimitPartition<string> PerMinute(string key, int permits) => RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Il limiter gira prima dell'autenticazione: si partiziona sull'hash della chiave presentata. Chi ne
    // inventa di nuove per avere altre partizioni resta comunque sotto il tetto per IP.
    static string DeviceOrIp(HttpContext http) =>
        http.Request.Headers.TryGetValue(ClinicalSyncClient.ApiKeyHeader, out var key) && key.Count == 1 && !string.IsNullOrEmpty(key[0])
            ? "key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key[0]!)), 0, 8)
            : "ip:" + ClientIp(http);
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
app.UseRequestLanguage();
app.UseExceptionHandler();
app.UseCsfRequestLogging();
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
    // Le interfacce della documentazione (solo se attivate) usano script in linea: niente CSP restrittiva lì.
    if (!ctx.Request.Path.StartsWithSegments("/scalar") && !ctx.Request.Path.StartsWithSegments("/swagger"))
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
api.MapAuth().MapPatients().MapSessions().MapDashboard().MapAudit().MapSystem().MapIngest();
app.MapFallback("/api/{**path}", () => Results.NotFound());
app.MapFhir();
app.MapHealth();
app.MapApiDocs();
if (ApiDocs.IsEnabled(app))
{
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
