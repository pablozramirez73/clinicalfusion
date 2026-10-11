using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ClinicalSensorFusion.Core.Ai;

/// <summary>
/// Client minimale dell'API REST di Ollama: elenco modelli (<c>GET /api/tags</c>, come l'app originale)
/// e chat in streaming (<c>POST /api/chat</c>, NDJSON). L'indirizzo è passato a ogni chiamata perché può
/// cambiare dalle impostazioni senza ricreare l'HttpClient.
/// </summary>
public sealed class OllamaClient(HttpClient http)
{
    public async Task<IReadOnlyList<OllamaModelInfo>> GetModelsAsync(Uri baseUri, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(new Uri(baseUri, "api/tags"), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var tags = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaTagsResponse, ct).ConfigureAwait(false);
        return tags?.Models ?? [];
    }

    /// <summary>Restituisce i frammenti di testo man mano che il modello li genera.</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        Uri baseUri,
        string model,
        IReadOnlyList<OllamaMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new OllamaChatRequest(model, [.. messages], Stream: true, new OllamaOptions(), KeepAlive: "10m");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/chat"))
        {
            Content = JsonContent.Create(body, OllamaJsonContext.Default.OllamaChatRequest),
        };

        // ResponseHeadersRead: senza, HttpClient attende l'intera risposta e lo streaming non serve a nulla.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            OllamaChatChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(line, OllamaJsonContext.Default.OllamaChatChunk);
            }
            catch (JsonException ex)
            {
                // Tipico dietro Cloudflare Access con sessione scaduta: arriva una pagina HTML di login.
                throw new OllamaException("Risposta non valida dal server (sessione di accesso remoto scaduta?).", ex);
            }

            if (chunk?.Error is { } error)
            {
                throw new OllamaException(error);
            }

            if (chunk?.Message?.Content is { Length: > 0 } text)
            {
                yield return text;
            }

            if (chunk?.Done == true)
            {
                yield break;
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is "text/html")
            {
                throw new OllamaException("Il server ha risposto con una pagina web: probabilmente serve l'accesso Cloudflare.");
            }

            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (detail.Length > 300)
        {
            detail = detail[..300];
        }

        throw new OllamaException($"Errore HTTP {(int)response.StatusCode} da Ollama: {detail}");
    }
}

public sealed class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }

    public OllamaException(string message, Exception inner) : base(message, inner) { }
}
