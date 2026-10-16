using ClinicalSensorFusion.Core.Security;

namespace ClinicalSensorFusion.Core.Tests;

/// <summary>Copertura in background e conferma delle operazioni sensibili.</summary>
public class AppLockPolicyTests
{
    [Theory]
    [InlineData(true, true, AppCover.Lock)]
    [InlineData(true, false, AppCover.Privacy)] // senza volto né codice non si può sbloccare: si copre soltanto
    [InlineData(false, true, AppCover.Privacy)] // blocco spento: i dati si coprono lo stesso
    [InlineData(false, false, AppCover.Privacy)]
    public void Data_are_always_covered_in_background(bool lockEnabled, bool available, AppCover expected) =>
        Assert.Equal(expected, AppLockPolicy.OnBackground(lockEnabled, available));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Sensitive_actions_ask_whenever_the_phone_can_authenticate(bool available, bool asks) =>
        Assert.Equal(asks, AppLockPolicy.RequiresConfirmation(available));

    private static readonly SensitiveSettings Saved =
        new("https://clinica.example.org/", "chiave", "192.168.1.20", null, null);

    [Fact]
    public void Same_values_need_no_confirmation()
    {
        Assert.False(Saved.DiffersFrom(Saved with { }));

        // Spazi ai lati e valori vuoti non sono una modifica.
        Assert.False(new SensitiveSettings(" https://clinica.example.org/ ", "chiave ", "192.168.1.20", "", "  ").DiffersFrom(Saved));
    }

    public static TheoryData<SensitiveSettings> Changes() =>
    [
        Saved with { SyncServer = "https://attacker.example.net/" },
        Saved with { SyncApiKey = "altra" },
        Saved with { SyncApiKey = null },
        Saved with { OllamaServer = "https://ai.example.net" },
        Saved with { CloudflareClientId = "id" },
        Saved with { CloudflareClientSecret = "segreto" },
    ];

    [Theory]
    [MemberData(nameof(Changes))]
    public void Any_change_of_server_or_credentials_needs_confirmation(SensitiveSettings changed) =>
        Assert.True(changed.DiffersFrom(Saved));
}
