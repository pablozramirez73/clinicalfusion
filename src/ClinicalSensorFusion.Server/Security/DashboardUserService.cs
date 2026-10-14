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
        var result = Hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, password);
        return user is not null && result != PasswordVerificationResult.Failed ? user : null;
    }

    public static string HashPassword(string password) => Hasher.HashPassword(null!, password);
}
