using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ClinicalSensorFusion.Server.Security;

/// <summary>Verifica delle credenziali della dashboard con PasswordHasher di ASP.NET Core Identity (PBKDF2).</summary>
public sealed class DashboardUserService(IOptionsMonitor<DashboardOptions> options)
{
    private static readonly PasswordHasher<DashboardUser> Hasher = new();

    // Hash di riferimento per eseguire comunque la verifica quando l'utente non esiste:
    // il tempo di risposta non rivela quali nomi utente sono validi.
    private static readonly string DummyHash = Hasher.HashPassword(null!, Guid.NewGuid().ToString());

    public DashboardUser? Validate(string userName, string password)
    {
        var user = options.CurrentValue.Users.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
        // Anche per un utente solo-Google si esegue la verifica, così il tempo non rivela che esiste.
        var hash = string.IsNullOrEmpty(user?.PasswordHash) ? DummyHash : user.PasswordHash;
        var result = Hasher.VerifyHashedPassword(user!, hash, password);
        return user is { PasswordHash.Length: > 0 } && result != PasswordVerificationResult.Failed ? user : null;
    }

    /// <summary>
    /// L'utente della dashboard associato a un accesso Google riuscito: indirizzo dichiarato verificato da Google
    /// e presente in <see cref="DashboardUser.GoogleEmail"/>. Null in ogni altro caso.
    /// </summary>
    public DashboardUser? FindByGoogle(string? email, bool emailVerified) =>
        !emailVerified || string.IsNullOrWhiteSpace(email)
            ? null
            : options.CurrentValue.Users.FirstOrDefault(u =>
                !string.IsNullOrWhiteSpace(u.GoogleEmail) && string.Equals(u.GoogleEmail.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Vero se l'indirizzo è di un utente della configurazione (come GoogleEmail o come nome utente).</summary>
    public bool IsConfiguredEmail(string email) => options.CurrentValue.Users.Any(u =>
        string.Equals(u.GoogleEmail?.Trim(), email, StringComparison.OrdinalIgnoreCase)
        || string.Equals(u.UserName, email, StringComparison.OrdinalIgnoreCase));

    public static string HashPassword(string password) => Hasher.HashPassword(null!, password);

    /// <summary>
    /// Verifica una password contro un hash; con hash assente esegue comunque una verifica, così il tempo di
    /// risposta non rivela se l'account esiste.
    /// </summary>
    public static bool VerifyPassword(string? hash, string password) =>
        Hasher.VerifyHashedPassword(null!, string.IsNullOrEmpty(hash) ? DummyHash : hash, password) != PasswordVerificationResult.Failed
        && !string.IsNullOrEmpty(hash);
}
