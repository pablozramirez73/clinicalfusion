namespace ClinicalSensorFusion.Server.Security;

/// <summary>Dispositivi autorizzati all'invio dei dati (sezione <c>Ingest</c>).</summary>
public sealed class IngestOptions
{
    public List<DeviceKey> Devices { get; init; } = [];
}

/// <summary>Si conserva solo l'hash SHA-256 della chiave: un dump della configurazione non la rivela.</summary>
public sealed class DeviceKey
{
    public required string Name { get; init; }

    public required string KeySha256 { get; init; }
}

/// <summary>Utenti della dashboard (sezione <c>Dashboard</c>).</summary>
public sealed class DashboardOptions
{
    public List<DashboardUser> Users { get; init; } = [];
}

public sealed class DashboardUser
{
    public required string UserName { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Hash PBKDF2 (formato ASP.NET Core Identity v3), generato con <c>dotnet run -- hash-password</c>.</summary>
    public required string PasswordHash { get; init; }
}

/// <summary>Limiti di frequenza (sezione <c>RateLimits</c>). I valori predefiniti stanno larghi sull'uso reale.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Tetto per indirizzo IP su tutte le rotte (una pagina della dashboard fa una decina di chiamate).</summary>
    public int GlobalPerMinute { get; init; } = 600;

    /// <summary>Tentativi di login per indirizzo IP, contro chi prova a indovinare le password.</summary>
    public int LoginPerMinute { get; init; } = 5;

    /// <summary>Metadati e lotti di blocchi per dispositivo (una rilevazione di 10 minuti sono ~20 richieste).</summary>
    public int IngestPerMinute { get; init; } = 300;

    /// <summary>Caricamenti audio contemporanei per dispositivo (l'app ne invia uno alla volta).</summary>
    public int ConcurrentAudioUploads { get; init; } = 2;
}

public static class Roles
{
    public const string Device = "Device";
    public const string Clinician = "Clinician";
}

public static class Policies
{
    public const string Ingest = "Ingest";
    public const string Clinician = "Clinician";
}
