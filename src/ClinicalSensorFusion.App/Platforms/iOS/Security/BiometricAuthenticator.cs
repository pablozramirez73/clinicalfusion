using ClinicalSensorFusion.App.Services;
using Foundation;
using LocalAuthentication;

namespace ClinicalSensorFusion.App.Platforms.iOS.Security;

/// <summary>
/// LocalAuthentication con la politica "proprietario del dispositivo": Face ID o Touch ID e, se falliscono
/// o non sono configurati, il codice del telefono. Così un volto non riconosciuto non chiude fuori l'operatore.
/// </summary>
internal sealed class BiometricAuthenticator : IDeviceAuthenticator
{
    private const LAPolicy Policy = LAPolicy.DeviceOwnerAuthentication;

    public bool IsAvailable
    {
        get
        {
            using var context = new LAContext();
            return context.CanEvaluatePolicy(Policy, out _);
        }
    }

    public string MethodName
    {
        get
        {
            using var context = new LAContext();
            context.CanEvaluatePolicy(Policy, out _);
            return context.BiometryType switch
            {
                LABiometryType.FaceId => "Face ID",
                LABiometryType.TouchId => "Touch ID",
                _ => "codice del telefono",
            };
        }
    }

    public async Task<bool> AuthenticateAsync(string reason)
    {
        using var context = new LAContext();
        if (!context.CanEvaluatePolicy(Policy, out _))
        {
            return false;
        }

        var (success, _) = await context.EvaluatePolicyAsync(Policy, reason);
        return success;
    }
}
