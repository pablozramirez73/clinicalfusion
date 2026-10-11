namespace ClinicalSensorFusion.App.Services;

/// <summary>Dialoghi astratti dai ViewModel (testabilità, nessuna dipendenza diretta da Shell/Page).</summary>
public interface IDialogService
{
    Task AlertAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string accept = "Sì", string cancel = "Annulla");
}

internal sealed class ShellDialogService : IDialogService
{
    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, "OK"));

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Sì", string cancel = "Annulla") =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, accept, cancel));
}
