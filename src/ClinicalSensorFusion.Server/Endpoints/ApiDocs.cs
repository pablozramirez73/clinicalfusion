using ClinicalSensorFusion.Core.Sync;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi;

namespace ClinicalSensorFusion.Server.Endpoints;

/// <summary>
/// Documentazione delle API: documento OpenAPI con gli schemi di sicurezza (JWT per i clinici, chiave per i
/// dispositivi) e Swagger UI su <c>/swagger</c>, dove «Authorize» accetta il token ottenuto da
/// <c>POST /api/v1/auth/token</c>. Attiva in sviluppo, altrove solo con <c>ApiDocs:Enabled=true</c>.
/// </summary>
public static class ApiDocs
{
    public const string ApiKeyScheme = "ApiKey";

    public static bool IsEnabled(WebApplication app) =>
        app.Configuration.GetValue("ApiDocs:Enabled", app.Environment.IsDevelopment());

    public static IServiceCollection AddCsfOpenApi(this IServiceCollection services) => services.AddOpenApi(o =>
        o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Clinical Sensor Fusion API";
            document.Info.Description =
                "Clinici: ottenere un token con POST /api/v1/auth/token e inserirlo in «Authorize». " +
                "Dispositivi: chiave nell'intestazione X-Api-Key (solo rotte di ingestione).";

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[JwtBearerDefaults.AuthenticationScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Token di POST /api/v1/auth/token.",
            };
            document.Components.SecuritySchemes[ApiKeyScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ClinicalSyncClient.ApiKeyHeader,
                Description = "Chiave del dispositivo.",
            };

            foreach (var (path, item) in document.Paths)
            {
                if (path.StartsWith("/api/v1/auth/login", StringComparison.Ordinal) || path.StartsWith("/api/v1/auth/token", StringComparison.Ordinal))
                {
                    continue;
                }

                var scheme = path.StartsWith("/api/v1/ingest", StringComparison.Ordinal) ? ApiKeyScheme : JwtBearerDefaults.AuthenticationScheme;
                if (item.Operations is null)
                {
                    continue;
                }

                foreach (var operation in item.Operations.Values)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, document)] = [] });
                }
            }

            return Task.CompletedTask;
        }));

    public static WebApplication MapApiDocs(this WebApplication app)
    {
        if (!IsEnabled(app))
        {
            return app;
        }

        app.MapOpenApi().AllowAnonymous();
        app.UseSwaggerUI(o =>
        {
            o.RoutePrefix = "swagger";
            o.SwaggerEndpoint("/openapi/v1.json", "Clinical Sensor Fusion API v1");
            o.DocumentTitle = "Clinical Sensor Fusion · API";
            o.EnablePersistAuthorization(); // il token resta al ricaricamento della pagina (solo in questo browser)
        });
        return app;
    }
}
