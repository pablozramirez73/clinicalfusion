using ClinicalSensorFusion.Core.Sync;
using Microsoft.Extensions.Logging;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Invio delle rilevazioni al server clinico (backend ASP.NET Core). Un solo invio alla volta; le
/// rilevazioni restano comunque sul telefono, cifrate, finché non sono state ricevute per intero.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Singleton per la vita dell'app; SemaphoreSlim senza AvailableWaitHandle non richiede Dispose.")]
public sealed class SyncService(SessionSynchronizer synchronizer, MauiAppSettings settings, ISecureStorage secureStorage, ILogger<SyncService> logger)
{
    public const string ApiKeyStorageKey = "sync.api_key";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsConfigured => TryGetServer(out _);

    /// <summary>Invio automatico al termine di una rilevazione (scelta dell'operatore nelle Impostazioni).</summary>
    public bool AutoSync => settings.AutoSync;

    /// <summary>Invia tutte le rilevazioni concluse non ancora sincronizzate.</summary>
    public async Task<SyncReport> SyncNowAsync(CancellationToken ct = default)
    {
        if (await CheckConfigurationAsync() is { } problem)
        {
            return problem;
        }

        if (!await _gate.WaitAsync(0, ct))
        {
            return new SyncReport(0, 0, 0, ["Sincronizzazione già in corso."]);
        }

        try
        {
            TryGetServer(out var server);
            return await synchronizer.SyncPendingAsync(server!, AppPaths.Audio, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Invia una sola rilevazione; se è in corso un altro invio aspetta il proprio turno.</summary>
    public async Task<SyncReport> SyncSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (await CheckConfigurationAsync() is { } problem)
        {
            return problem;
        }

        await _gate.WaitAsync(ct);
        try
        {
            TryGetServer(out var server);
            var report = await synchronizer.SyncSessionAsync(server!, sessionId, AppPaths.Audio, ct);
            if (report.Errors.Count > 0)
            {
                // Solo il numero: il testo degli errori è per l'operatore e può contenere il codice soggetto.
                logger.LogWarning("Invio della rilevazione {SessionId} incompleto: {ErrorCount} errori.", sessionId, report.Errors.Count);
            }

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Controllo del payload JSON che verrebbe inviato per questa rilevazione (non richiede il server).</summary>
    public Task<PayloadReport> InspectPayloadAsync(ClinicalSensorFusion.Core.Domain.ClinicalSession session, CancellationToken ct = default) =>
        synchronizer.InspectPayloadAsync(session, ct);

    /// <summary>Pagina della dashboard con i grafici della rilevazione; null se il server non è configurato.</summary>
    public Uri? DashboardUrlFor(string sessionId) =>
        TryGetServer(out var server) ? new Uri(server!, $"sessions/{Uri.EscapeDataString(sessionId)}") : null;

    private async Task<SyncReport?> CheckConfigurationAsync()
    {
        if (!IsConfigured)
        {
            return new SyncReport(0, 0, 0, ["Server clinico non configurato nelle Impostazioni."]);
        }

        return string.IsNullOrEmpty(await secureStorage.GetAsync(ApiKeyStorageKey))
            ? new SyncReport(0, 0, 0, ["Chiave del dispositivo mancante nelle Impostazioni."])
            : null;
    }

    private bool TryGetServer(out Uri? server)
    {
        server = null;
        if (!Uri.TryCreate(settings.SyncServer?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        server = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        return true;
    }
}

/// <summary>Aggiunge la chiave del dispositivo (dal Portachiavi) alle richieste verso il server clinico.</summary>
public sealed class DeviceApiKeyHandler(ISecureStorage secureStorage) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = await secureStorage.GetAsync(SyncService.ApiKeyStorageKey).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.TryAddWithoutValidation(ClinicalSyncClient.ApiKeyHeader, key);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
