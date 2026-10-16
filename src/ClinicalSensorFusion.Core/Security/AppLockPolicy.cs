namespace ClinicalSensorFusion.Core.Security;

/// <summary>Cosa copre l'app quando non è in primo piano.</summary>
public enum AppCover
{
    /// <summary>Solo una copertura: nasconde i dati nel selettore delle app e sparisce al ritorno.</summary>
    Privacy,

    /// <summary>Blocco: al ritorno serve farsi riconoscere (Face ID, Touch ID o codice del telefono).</summary>
    Lock,
}

/// <summary>Regole del blocco dell'app e della conferma per le operazioni sensibili.</summary>
public static class AppLockPolicy
{
    /// <summary>
    /// In background i dati si coprono sempre, anche con il blocco spento: l'anteprima nel selettore delle app
    /// è una foto dello schermo. Il blocco vero serve che sia attivo e che il telefono abbia un modo per sbloccare.
    /// </summary>
    public static AppCover OnBackground(bool lockEnabled, bool authenticationAvailable) =>
        lockEnabled && authenticationAvailable ? AppCover.Lock : AppCover.Privacy;

    /// <summary>
    /// Un'operazione sensibile (dove vanno i dati, con quali credenziali, cancellazioni) chiede di farsi
    /// riconoscere anche con il blocco spento: chi ha in mano il telefono sbloccato non deve poter dirottare
    /// l'invio dei dati clinici. Se il telefono non ha né biometria né codice non c'è nulla da chiedere.
    /// </summary>
    public static bool RequiresConfirmation(bool authenticationAvailable) => authenticationAvailable;
}

/// <summary>
/// Le impostazioni che decidono dove vanno i dati clinici, con quali credenziali e quale certificato del
/// server è accettato. Cambiarle chiede conferma
/// (<see cref="AppLockPolicy.RequiresConfirmation"/>).
/// </summary>
public sealed record SensitiveSettings(
    string? SyncServer,
    string? SyncApiKey,
    string? OllamaServer,
    string? CloudflareClientId,
    string? CloudflareClientSecret,
    string? SyncServerPin = null)
{
    /// <summary>Vero se almeno un valore è cambiato. Spazi ai lati e valore vuoto contano come assente.</summary>
    public bool DiffersFrom(SensitiveSettings other) =>
        !Same(SyncServer, other.SyncServer)
        || !Same(SyncApiKey, other.SyncApiKey)
        || !Same(OllamaServer, other.OllamaServer)
        || !Same(CloudflareClientId, other.CloudflareClientId)
        || !Same(CloudflareClientSecret, other.CloudflareClientSecret)
        || !Same(SyncServerPin, other.SyncServerPin);

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal);
}
