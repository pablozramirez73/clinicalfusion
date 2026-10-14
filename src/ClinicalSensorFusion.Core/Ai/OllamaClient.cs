using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.Core.Ai;

/// <summary>
/// Client minimale dell'API REST di Ollama: elenco modelli (<c>GET /api/tags</c>, come l'app originale)
/// e chat in streaming (<c>POST /api/chat</c>, NDJSON). L'indirizzo è passato a ogni chiamata perché può
/// cambiare dalle impostazioni senza ricreare l'HttpClient.
/// </summary>
public sealed class OllamaClient(HttpClient http, ILogger<OllamaClient>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<OllamaClient>.Instance;

    public async Task<IReadOnlyList<OllamaModelInfo>> GetModelsAsync(Uri baseUri, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(new Uri(baseUri, "api/tags"), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var tags = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaTagsResponse, ct).ConfigureAwait(false);
        return tags?.Models ?? [];
    }

    /// <summary>
    /// Attesa massima tra un frammento e il successivo. Il timeout dell'HttpClient copre solo l'attesa
    /// dell'intestazione: senza questo limite uno stream che si blocca a metà resterebbe appeso per sempre.
    /// </summary>
    public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Restituisce i frammenti di testo man mano che il modello li genera.</summary>
    /// <exception cref="OllamaException">
    /// Errore del server, timeout di rete o stream chiuso prima del frammento finale (<c>done</c>): in tutti
    /// questi casi il testo già restituito è incompleto. <see cref="OperationCanceledException"/> è riservata
    /// all'annullamento richiesto dal chiamante.
    /// </exception>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        Uri baseUri,
        string model,
        IReadOnlyList<OllamaMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Il nome del modello è configurazione, non un dato del paziente; il prompt non si registra mai.
        using var operation = CsfTelemetry.StartOperation(_logger, "ai.chat", CsfTelemetry.Ai.RequestDuration).Tag("model", model);
        var characters = 0L;
        var completed = false;
        try
        {
            await foreach (var text in ChatStreamCoreAsync(baseUri, model, messages, ct).ConfigureAwait(false))
            {
                if (characters == 0)
                {
                    CsfTelemetry.Ai.TimeToFirstToken.Record(operation.Elapsed.TotalSeconds);
                }

                characters += text.Length;
                yield return text;
            }

            CsfTelemetry.Ai.ResponseSize.Record(characters);
            operation.Succeed();
            completed = true;
        }
        finally
        {
            // Un iteratore non può avere catch attorno a yield: l'esito diverso da "riuscito" si deduce qui.
            if (!completed && ct.IsCancellationRequested)
            {
                operation.Fail(new OperationCanceledException(ct), cancelledByCaller: true);
            }
        }
    }

    private async IAsyncEnumerable<string> ChatStreamCoreAsync(
        Uri baseUri,
        string model,
        IReadOnlyList<OllamaMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new OllamaChatRequest(model, [.. messages], Stream: true, new OllamaOptions(), KeepAlive: "10m");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/chat"))
        {
            Content = JsonContent.Create(body, OllamaJsonContext.Default.OllamaChatRequest),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (await ReadLineAsync(reader, idle, ct).ConfigureAwait(false) is { } line)
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
                throw new OllamaException(Loc.T("Risposta non valida dal server (sessione di accesso remoto scaduta?)."), ex);
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

        // Fine dello stream senza "done": connessione chiusa a metà (proxy, rete, Ollama riavviato).
        throw new OllamaException(Loc.T("La risposta del modello si è interrotta prima della fine: il testo ricevuto è incompleto."));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            // ResponseHeadersRead: senza, HttpClient attende l'intera risposta e lo streaming non serve a nulla.
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout dell'HttpClient: non è un annullamento dell'utente.
            throw new OllamaException(Loc.T("Il server Ollama non ha risposto in tempo."), ex);
        }
    }

    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationTokenSource idle, CancellationToken ct)
    {
        idle.CancelAfter(StreamIdleTimeout);
        try
        {
            return await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OllamaException(Loc.T("Il modello ha smesso di rispondere: il testo ricevuto è incompleto."), ex);
        }
        catch (IOException ex)
        {
            throw new OllamaException(Loc.T("Connessione con Ollama interrotta: il testo ricevuto è incompleto."), ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is "text/html")
            {
                throw new OllamaException(Loc.T("Il server ha risposto con una pagina web: probabilmente serve l'accesso Cloudflare."));
            }

            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (detail.Length > 300)
        {
            detail = detail[..300];
        }

        throw new OllamaException(Loc.F("Errore HTTP {0} da Ollama: {1}", (int)response.StatusCode, detail));
    }
}

public sealed class OllamaException : Exception
{
    public OllamaException(string message) : base(message) { }

    public OllamaException(string message, Exception inner) : base(message, inner) { }
}
