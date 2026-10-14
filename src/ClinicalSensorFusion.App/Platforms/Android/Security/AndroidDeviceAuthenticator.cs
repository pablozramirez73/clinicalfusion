using ClinicalSensorFusion.Core.Localization;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using ClinicalSensorFusion.App.Services;

namespace ClinicalSensorFusion.App.Platforms.Android.Security;

/// <summary>
/// BiometricPrompt di sistema con impronta o volto e, come riserva, il blocco schermo del telefono (PIN,
/// sequenza o password). Così una biometria non riconosciuta non chiude fuori l'operatore.
/// </summary>
internal sealed class AndroidDeviceAuthenticator : IDeviceAuthenticator
{
    private const int Biometric = (int)BiometricManagerAuthenticators.BiometricWeak;
    private const int BiometricOrCredential = Biometric | (int)BiometricManagerAuthenticators.DeviceCredential;

    private static BiometricManager? Manager =>
        global::Android.App.Application.Context.GetSystemService(Context.BiometricService) as BiometricManager;

    public bool IsAvailable => Manager?.CanAuthenticate(BiometricOrCredential) == BiometricCode.Success;

    public string MethodName => Manager?.CanAuthenticate(Biometric) == BiometricCode.Success
        ? Loc.T("impronta o volto")
        : Loc.T("codice del telefono");

    public Task<bool> AuthenticateAsync(string reason)
    {
        if (!IsAvailable || Platform.CurrentActivity is not { } activity)
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var prompt = new BiometricPrompt.Builder(activity)
                    .SetTitle("SensorFusion Clinic")
                    .SetDescription(reason)
                    .SetAllowedAuthenticators(BiometricOrCredential)
                    .Build();
                prompt.Authenticate(new CancellationSignal(), activity.MainExecutor!, new Callback(completion));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    private sealed class Callback(TaskCompletionSource<bool> completion) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => completion.TrySetResult(true);

        // Errore definitivo o annullamento. OnAuthenticationFailed (tentativo non riconosciuto) non chiude
        // la richiesta: il sistema lascia riprovare.
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) => completion.TrySetResult(false);
    }
}
