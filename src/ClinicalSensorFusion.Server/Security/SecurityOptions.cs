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
