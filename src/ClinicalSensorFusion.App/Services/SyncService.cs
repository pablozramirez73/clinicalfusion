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

    public async Task<SyncReport> SyncNowAsync(CancellationToken ct = default)
    {
        if (!TryGetServer(out var server))
        {
            return new SyncReport(0, 0, 0, ["Server clinico non configurato nelle Impostazioni."]);
        }

        if (string.IsNullOrEmpty(await secureStorage.GetAsync(ApiKeyStorageKey)))
        {
            return new SyncReport(0, 0, 0, ["Chiave del dispositivo mancante nelle Impostazioni."]);
        }

        if (!await _gate.WaitAsync(0, ct))
        {
            return new SyncReport(0, 0, 0, ["Sincronizzazione già in corso."]);
        }

        try
        {
            return await synchronizer.SyncPendingAsync(server!, AppPaths.Audio, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Invio in background dopo una rilevazione: gli errori si registrano, non interrompono l'operatore.</summary>
    public void SyncInBackground()
    {
        if (!settings.AutoSync || !IsConfigured)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var report = await SyncNowAsync();
                if (report.Errors.Count > 0)
                {
                    logger.LogWarning("Sincronizzazione automatica incompleta: {Errors}", string.Join("; ", report.Errors));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sincronizzazione automatica fallita.");
            }
        });
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
