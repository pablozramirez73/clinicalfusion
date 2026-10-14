namespace ClinicalSensorFusion.Core.Persistence;

/// <summary>
/// Fornisce la chiave di cifratura del database (32 byte casuali). Su iOS è custodita nel Portachiavi,
/// accessibile solo con dispositivo sbloccato e non migrabile su altri dispositivi.
/// </summary>
public interface IDatabaseKeyProvider
{
    Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default);
}
