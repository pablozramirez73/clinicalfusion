using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Server.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalSensorFusion.Server.Endpoints;

/// <summary>
/// Inoltro verso Ollama per l'app. Ollama parla solo HTTP in chiaro: invece di esporlo sulla rete, l'app
/// chiama il server clinico in HTTPS (con la sua chiave del dispositivo) e il server inoltra a Ollama sulla
/// stessa macchina o su una rete fidata (<c>Ai:OllamaUrl</c>). Passano solo le due chiamate che l'app usa.
/// </summary>
public static class AiProxyEndpoints
{
    public const string HttpClientName = "ollama";

    /// <summary>Un prompt con il Bundle FHIR di una rilevazione resta sotto qualche centinaio di kB.</summary>
    private const long MaxRequestBytes = 4L * 1024 * 1024;

    public static IServiceCollection AddAiProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(HttpClientName, client =>
        {
            if (Uri.TryCreate(configuration["Ai:OllamaUrl"], UriKind.Absolute, out var ollama))
            {
                client.BaseAddress = ollama.AbsoluteUri.EndsWith('/') ? ollama : new Uri(ollama.AbsoluteUri + "/");
            }

            // Un modello locale può impiegare minuti su prompt lunghi.
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        return services;
    }

    public static RouteGroupBuilder MapAiProxy(this RouteGroupBuilder api)
    {
        // Il percorso ripete "api/" di Ollama: l'app usa come indirizzo https://server/api/v1/ai/ e aggiunge
        // "api/tags" o "api/chat" come farebbe con Ollama.
        var g = api.MapGroup("/ai/api").WithTags("Intelligenza artificiale (inoltro a Ollama)")
            .RequireAuthorization(Policies.Ingest)
            .RequireRateLimiting(IngestEndpoints.RateLimit);
        g.MapGet("/tags", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
            ForwardAsync(http, clients, HttpMethod.Get, "api/tags", ct));
        g.MapPost("/chat", (HttpContext http, IHttpClientFactory clients, CancellationToken ct) =>
                ForwardAsync(http, clients, HttpMethod.Post, "api/chat", ct))
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes))
            .DisableAntiforgery();
        return api;
    }

    private static async Task ForwardAsync(HttpContext http, IHttpClientFactory clients, HttpMethod method, string path, CancellationToken ct)
    {
        var client = clients.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
        {
            await Results.Problem(Loc.T("Ollama non è configurato sul server (Ai:OllamaUrl).", Ui.Culture), statusCode: StatusCodes.Status503ServiceUnavailable)
                .ExecuteAsync(http);
            return;
        }

        using var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            request.Content = new StreamContent(http.Request.Body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage response;
        try
        {
            // Lo streaming della risposta (una riga JSON per frammento) arriva all'app man mano.
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            await Results.Problem(Loc.T("Ollama non raggiungibile dal server.", Ui.Culture), statusCode: StatusCodes.Status502BadGateway)
                .ExecuteAsync(http);
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            await body.CopyToAsync(http.Response.Body, ct);
        }
    }
}
