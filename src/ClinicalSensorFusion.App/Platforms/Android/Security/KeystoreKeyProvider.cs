using ClinicalSensorFusion.Core.Localization;
using System.Security.Cryptography;
using Android.Security.Keystore;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Persistence;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace ClinicalSensorFusion.App.Platforms.Android.Security;

/// <summary>
/// Chiave del database protetta dall'Android Keystore.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Il Keystore custodisce una chiave AES che non lascia mai l'hardware sicuro del telefono; con quella
/// si cifra (AES-GCM) la chiave casuale del database, salvata nella cartella privata dell'app.</item>
/// <item>Nessun requisito di autenticazione sulla chiave: una rilevazione in corso deve poter chiudere i
/// blocchi anche a schermo bloccato (equivalente di <c>AfterFirstUnlockThisDeviceOnly</c> su iOS).</item>
/// <item>La chiave non è nei backup (vedi il manifest) e non si trasferisce su un altro telefono. Se va
/// persa, il DB esistente non è più leggibile: è il comportamento voluto per dati sanitari, e
/// <see cref="ClinicalDatabase"/> rileva il caso senza sovrascrivere il file.</item>
/// </list>
/// </remarks>
internal sealed class KeystoreKeyProvider : IDatabaseKeyProvider
{
    private const string Store = "AndroidKeyStore";
    private const string Alias = "com.lucaguelfi.sensorfusionclinic.database";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvLength = 12;
    private const int TagBits = 128;

    private static string WrappedKeyPath => Path.Combine(AppPaths.Library, "clinical.db.key");

    public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken = default)
    {
        var keyStore = KeyStore.GetInstance(Store) ?? throw new InvalidOperationException("Android Keystore non disponibile.");
        keyStore.Load(null);

        if (File.Exists(WrappedKeyPath))
        {
            // Chiave avvolta presente ma non decifrabile: errore, mai una chiave nuova al suo posto.
            var wrapping = keyStore.GetKey(Alias, null)
                ?? throw new InvalidOperationException(Loc.T("La chiave del database non è più nel Keystore del telefono."));
            return Task.FromResult(Unwrap(wrapping, File.ReadAllBytes(WrappedKeyPath)));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = Wrap(CreateWrappingKey(), key);

        // Scrittura atomica: un file a metà renderebbe il database illeggibile per sempre.
        var temporary = WrappedKeyPath + ".tmp";
        File.WriteAllBytes(temporary, wrapped);
        File.Move(temporary, WrappedKeyPath, overwrite: true);
        return Task.FromResult(key);
    }

    private static IKey CreateWrappingKey()
    {
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Store)
            ?? throw new InvalidOperationException(Loc.T("Generatore di chiavi del Keystore non disponibile."));
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return generator.GenerateKey() ?? throw new InvalidOperationException(Loc.T("Impossibile creare la chiave nel Keystore."));
    }

    private static byte[] Wrap(IKey wrapping, byte[] key)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, wrapping);
        var iv = cipher.GetIV()!;
        var encrypted = cipher.DoFinal(key)!;
        return [.. iv, .. encrypted];
    }

    private static byte[] Unwrap(IKey wrapping, byte[] wrapped)
    {
        if (wrapped.Length <= IvLength)
        {
            throw new InvalidOperationException(Loc.T("File della chiave del database danneggiato."));
        }

        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, wrapping, new GCMParameterSpec(TagBits, wrapped, 0, IvLength));
        return cipher.DoFinal(wrapped, IvLength, wrapped.Length - IvLength)!;
    }
}
