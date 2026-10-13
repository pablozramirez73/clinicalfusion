using ClinicalSensorFusion.Core.Localization;

namespace ClinicalSensorFusion.App.Services;

/// <summary>Dialoghi astratti dai ViewModel (testabilità, nessuna dipendenza diretta da Shell/Page).</summary>
public interface IDialogService
{
    Task AlertAsync(string title, string message);

    /// <param name="accept">Testo del pulsante di conferma; null = «Sì» nella lingua dell'interfaccia.</param>
    /// <param name="cancel">Testo del pulsante di annullamento; null = «Annulla» nella lingua dell'interfaccia.</param>
    Task<bool> ConfirmAsync(string title, string message, string? accept = null, string? cancel = null);
}

internal sealed class ShellDialogService : IDialogService
{
    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(Loc.T(title), Loc.T(message), "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Sì", string cancel = "Annulla") =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(Loc.T(title), Loc.T(message), Loc.T(accept), Loc.T(cancel)));
}
