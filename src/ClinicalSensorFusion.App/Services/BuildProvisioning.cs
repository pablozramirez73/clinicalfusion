using System.Reflection;

namespace ClinicalSensorFusion.App.Services;

/// <summary>
/// Preconfigurazione del server clinico decisa al momento della compilazione
/// (<c>-p:ProvisionSyncServer=… -p:ProvisionSyncApiKey=…</c>): l'app si installa già collegata, senza
/// digitare indirizzo e chiave sul telefono. I valori non stanno nel repository: arrivano dalla riga di
/// comando e finiscono solo in quella build.
/// </summary>
internal static class BuildProvisioning
{
    private const string AppliedKey = "sync.provisioned";

    /// <summary>
    /// Applica i valori al primo avvio di ogni build preconfigurata: se poi l'operatore li cambia a mano
    /// nelle Impostazioni, restano i suoi finché non viene installata un'altra build preconfigurata.
    /// </summary>
    public static async Task ApplyAsync(MauiAppSettings settings, ISecureStorage secureStorage, IPreferences preferences)
    {
        var server = Metadata("ProvisionSyncServer");
        var apiKey = Metadata("ProvisionSyncApiKey");
        if (string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        // Una build preconfigurata si applica una volta sola: l'identificativo del modulo cambia a ogni compilazione.
        var stamp = typeof(BuildProvisioning).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        if (preferences.Get(AppliedKey, string.Empty) == stamp)
        {
            return;
        }

        settings.SyncServer = server;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await secureStorage.SetAsync(SyncService.ApiKeyStorageKey, apiKey);
        }

        preferences.Set(AppliedKey, stamp);
    }

    private static string? Metadata(string key) => typeof(BuildProvisioning).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;
}
