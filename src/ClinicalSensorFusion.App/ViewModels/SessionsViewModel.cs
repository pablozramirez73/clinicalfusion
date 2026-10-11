using System.Collections.ObjectModel;
using System.Globalization;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

public sealed record SessionListItem(string Id, string Subject, string When, string Details, bool Completed);

/// <summary>Elenco delle rilevazioni con ricerca per codice soggetto.</summary>
public sealed partial class SessionsViewModel(
    ClinicalRepository repository,
    CaptureCoordinator coordinator,
    IDialogService dialogs) : ObservableObject
{
    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var sessions = await repository.ListSessionsAsync(SearchText);
            Sessions.Clear();
            foreach (var s in sessions)
            {
                Sessions.Add(ToItem(s));
            }

            IsEmpty = Sessions.Count == 0;
        }
        catch (DatabaseKeyMismatchException)
        {
            ErrorMessage = "L'archivio clinico esistente non è decifrabile con la chiave di questo dispositivo/installazione. " +
                           "Il file non è stato modificato. Vedere docs/architettura.md, sezione \"Migrazione dall'app originale\".";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenAsync(SessionListItem item) =>
        Shell.Current.GoToAsync(nameof(SessionDetailPage), new Dictionary<string, object> { ["SessionId"] = item.Id });

    [RelayCommand]
    private async Task DeleteAsync(SessionListItem item)
    {
        if (!await dialogs.ConfirmAsync("Elimina rilevazione", $"Eliminare definitivamente la rilevazione di {item.Subject} ({item.When}) con audio e analisi?", "Elimina"))
        {
            return;
        }

        await coordinator.DeleteAsync(item.Id);
        Sessions.Remove(item);
        IsEmpty = Sessions.Count == 0;
    }

    partial void OnSearchTextChanged(string value) => LoadCommand.Execute(null);

    private static SessionListItem ToItem(ClinicalSession s)
    {
        var duration = s.Duration is { } d ? d.ToString(@"mm\:ss", CultureInfo.CurrentCulture) : "incompleta";
        var mode = s.Mode switch
        {
            CaptureMode.BodyLidar => "Corpo",
            CaptureMode.HandsLidar => "Mani",
            CaptureMode.FaceTrueDepth => "Volto",
            _ => s.Mode.ToString(),
        };
        return new SessionListItem(
            s.Id,
            s.SubjectCode,
            s.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            $"{mode} · {duration} · {s.OperatorName}",
            s.EndedAt is not null);
    }
}
