using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.Core.Ai;
using ClinicalSensorFusion.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

/// <summary>
/// Login interattivo verso un server Ollama protetto (es. Cloudflare Access) dentro una WebView, come
/// faceva l'app originale; al termine i cookie vengono copiati nel client HTTP.
/// </summary>
public sealed partial class RemoteAccessViewModel(IWebSessionBridge bridge, IDialogService dialogs) : ObservableObject, IQueryAttributable
{
    [ObservableProperty]
    public partial string? Url { get; set; }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("Url", out var value) && value is string raw && OllamaEndpoint.TryParse(raw, out var uri) && uri is not null)
        {
            // Si apre l'endpoint /api/tags: dopo il login la pagina mostra il JSON dei modelli (conferma visiva).
            Url = new Uri(uri, "api/tags").ToString();
        }
    }

    [RelayCommand]
    private async Task DoneAsync()
    {
        if (Url is null || !Uri.TryCreate(Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var count = await bridge.SyncCookiesAsync(uri);
        await dialogs.AlertAsync(Loc.T("Accesso remoto"), count > 0
            ? Loc.F("Sessione acquisita ({0} cookie). Ora l'analisi può raggiungere il server.", count)
            : Loc.T("Nessun cookie trovato: completare prima il login nella pagina."));
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await bridge.ClearAsync();
        await dialogs.AlertAsync(Loc.T("Accesso remoto"), Loc.T("Sessione e cookie eliminati."));
    }
}
