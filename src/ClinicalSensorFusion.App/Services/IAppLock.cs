using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Services;

/// <summary>Verifica dell'identità di chi apre l'app (Face ID o Touch ID, con il codice del telefono come riserva).</summary>
public interface IDeviceAuthenticator
{
    /// <summary>Falso se sul telefono non è impostato né un volto/impronta né un codice.</summary>
    bool IsAvailable { get; }

    /// <summary>"Face ID", "Touch ID" oppure "codice del telefono", per i testi dell'interfaccia.</summary>
    string MethodName { get; }

    /// <summary>Mostra la richiesta di sistema; vero solo se l'utente è stato riconosciuto.</summary>
    Task<bool> AuthenticateAsync(string reason);
}

/// <summary>
/// Copertura a tutto schermo mostrata sopra l'app mentre è bloccata. È una finestra nativa separata: copre
/// anche avvisi e pagine modali e non dipende dallo stato della navigazione.
/// </summary>
public interface ILockScreen
{
    /// <param name="unlockTitle">Testo del pulsante (es. "Sblocca con Face ID").</param>
    /// <param name="unlockRequested">Chiamato quando l'utente tocca il pulsante.</param>
    void Show(string unlockTitle, Action unlockRequested);

    void SetMessage(string message);

    void Hide();
}

/// <summary>
/// Blocco dell'app: se l'operatore lo attiva, all'avvio e a ogni ritorno dal background il contenuto resta
/// coperto finché Face ID (o il codice del telefono) non riconosce l'utente.
/// </summary>
public sealed class AppLockController(IDeviceAuthenticator authenticator, ILockScreen lockScreen, MauiAppSettings settings)
{
    private bool _locked;
    private bool _authenticating;

    public bool IsEnabled => settings.RequireDeviceAuthentication;

    public IDeviceAuthenticator Authenticator => authenticator;

    public void Attach(Window window)
    {
        window.Created += (_, _) =>
        {
            Lock();
            _ = UnlockAsync();
        };

        // Stopped e non Deactivated: la richiesta di Face ID disattiva la finestra senza mandarla in
        // background, e bloccare lì farebbe ripartire la richiesta all'infinito. Qui si copre soltanto,
        // così i dati non compaiono nell'anteprima del selettore delle app; la richiesta parte al ritorno.
        window.Stopped += (_, _) => Lock();
        window.Resumed += (_, _) => _ = UnlockAsync();
    }

    /// <summary>Attiva o disattiva il blocco. Per attivarlo (e per toglierlo) serve farsi riconoscere.</summary>
    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (enabled == IsEnabled)
        {
            return true;
        }

        if (!authenticator.IsAvailable)
        {
            return false;
        }

        var reason = enabled ? Loc.T("Conferma per proteggere l'app") : Loc.T("Conferma per togliere la protezione");
        if (!await authenticator.AuthenticateAsync(reason))
        {
            return false;
        }

        settings.RequireDeviceAuthentication = enabled;
        return true;
    }

    private void Lock()
    {
        // Senza volto né codice sul telefono non c'è modo di sbloccare: meglio non chiudere fuori l'operatore.
        if (_locked || !IsEnabled || !authenticator.IsAvailable)
        {
            return;
        }

        _locked = true;
        lockScreen.Show(Loc.F("Sblocca con {0}", Loc.T(authenticator.MethodName)), () => _ = UnlockAsync());
    }

    private async Task UnlockAsync()
    {
        if (!_locked || _authenticating)
        {
            return;
        }

        _authenticating = true;
        try
        {
            if (await authenticator.AuthenticateAsync(Loc.T("Sblocca SensorFusion Clinic")))
            {
                _locked = false;
                lockScreen.Hide();
            }
            else
            {
                lockScreen.SetMessage(Loc.T("Non riconosciuto. Riprova per accedere ai dati clinici."));
            }
        }
        catch (Exception ex)
        {
            lockScreen.SetMessage(Loc.F("Sblocco non riuscito: {0}", ex.Message));
        }
        finally
        {
            _authenticating = false;
        }
    }
}
