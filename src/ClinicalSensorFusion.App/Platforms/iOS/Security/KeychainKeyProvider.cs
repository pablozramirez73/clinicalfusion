using System.Security.Cryptography;
using ClinicalSensorFusion.Core.Localization;
using ClinicalSensorFusion.Core.Persistence;
using Foundation;
using Security;

namespace ClinicalSensorFusion.App.Platforms.iOS.Security;

/// <summary>
/// Chiave del database nel Portachiavi iOS.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>AfterFirstUnlockThisDeviceOnly</c>: leggibile anche a schermo bloccato dopo il primo sblocco
/// (una rilevazione in corso può chiudere i blocchi dopo il blocco automatico), mai migrata su un altro
/// dispositivo né inclusa nei backup.</item>
/// <item>Se la chiave va persa (app reinstallata con un altro team di firma), il DB esistente non è più
/// leggibile: è il comportamento voluto per dati sanitari. <see cref="ClinicalDatabase"/> rileva il caso
/// e non sovrascrive il file.</item>
/// </list>
/// </remarks>
internal sealed class KeychainKeyProvider : IDatabaseKeyProvider
{
    private const string Service = "com.lucaguelfi.clinicalsensorfusion.database";
    private const string Account = "clinical.db";

    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        var query = new SecRecord(SecKind.GenericPassword) { Service = Service, Account = Account };
        var existing = SecKeyChain.QueryAsRecord(query, out var status);
        if (status == SecStatusCode.Success && existing?.ValueData is { Length: 32 } data)
        {
            return Task.FromResult(data.ToArray());
        }

        if (status != SecStatusCode.ItemNotFound && status != SecStatusCode.Success)
        {
            throw new InvalidOperationException(Loc.F("Portachiavi non accessibile ({0}).", status));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = Account,
            ValueData = NSData.FromArray(key),
            Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
            Synchronizable = false,
        };

        var added = SecKeyChain.Add(record);
        if (added != SecStatusCode.Success)
        {
            throw new InvalidOperationException(Loc.F("Impossibile salvare la chiave nel Portachiavi ({0}).", added));
        }

        return Task.FromResult(key);
    }
}
