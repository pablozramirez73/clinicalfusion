using System.Collections.ObjectModel;
using System.Globalization;
using ClinicalSensorFusion.App.Services;
using ClinicalSensorFusion.App.Views;
using ClinicalSensorFusion.Core.Domain;
using ClinicalSensorFusion.Core.Persistence;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClinicalSensorFusion.App.ViewModels;

public sealed record SessionListItem(string Id, string Subject, CaptureMode Mode, string When, string Details, bool Completed, bool Synced);

/// <summary>Elenco delle rilevazioni con ricerca per codice soggetto.</summary>
public sealed partial class SessionsViewModel(
    ClinicalRepository repository,
    CaptureCoordinator coordinator,
    SyncService sync,
    IDialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string? SyncStatus { get; set; }

    private bool CanSync() => !IsSyncing;

    /// <summary>Invia al server clinico le rilevazioni non ancora sincronizzate.</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        IsSyncing = true;
        SyncStatus = "Invio in corso…";
        try
        {
            var report = await sync.SyncNowAsync();
            SyncStatus = report switch
            {
                { Errors.Count: > 0 } => string.Join(" ", report.Errors),
                { Sessions: 0 } => "Nessuna rilevazione da inviare: il server è già aggiornato.",
                _ => $"Inviate {report.Sessions} rilevazioni ({report.Chunks} blocchi, {report.AudioFiles} audio).",
            };
        }
        catch (Exception ex)
        {
            SyncStatus = ex.Message;
        }
        finally
        {
            IsSyncing = false;
        }

        // L'elenco mostra quali rilevazioni risultano inviate.
        await LoadAsync();
    }

    public ObservableCollection<SessionListItem> Sessions { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    /// <summary>Sottotitolo dell'intestazione: quante rilevazioni mostra l'elenco.</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

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

            RefreshCount();
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
        RefreshCount();
    }

    [RelayCommand]
    private static Task NewCaptureAsync() => Shell.Current.GoToAsync("//capture");

    private void RefreshCount()
    {
        IsEmpty = Sessions.Count == 0;
        CountText = Sessions.Count switch
        {
            0 => string.IsNullOrWhiteSpace(SearchText) ? "Archivio cifrato su questo dispositivo" : "Nessun risultato",
            1 => "1 rilevazione",
            var n => string.Create(CultureInfo.CurrentCulture, $"{n} rilevazioni"),
        };
    }

    partial void OnSearchTextChanged(string value) => LoadCommand.Execute(null);

    private static SessionListItem ToItem(ClinicalSession s)
    {
        // Senza durata la rilevazione è incompleta: lo dice già il chip dell'elenco.
        var details = s.Duration is { } d
            ? $"{ModeLabel(s.Mode)} · {d.ToString(@"mm\:ss", CultureInfo.CurrentCulture)} · {s.OperatorName}"
            : $"{ModeLabel(s.Mode)} · {s.OperatorName}";
        return new SessionListItem(
            s.Id,
            s.SubjectCode,
            s.Mode,
            FriendlyDate(s.StartedAt.ToLocalTime()),
            details,
            s.EndedAt is not null,
            s.SyncedAt is not null);
    }

    internal static string ModeLabel(CaptureMode mode) => mode switch
    {
        CaptureMode.BodyLidar => "Corpo",
        CaptureMode.HandsLidar => "Mani",
        CaptureMode.FaceTrueDepth => "Volto",
        _ => mode.ToString(),
    };

    /// <summary>"Oggi, 14:32" / "Ieri, 09:10" / "12 ott, 14:32": più leggibile della data completa in un elenco.</summary>
    private static string FriendlyDate(DateTimeOffset local)
    {
        var c = CultureInfo.CurrentCulture;
        var days = (DateTimeOffset.Now.Date - local.Date).Days;
        var day = days switch
        {
            0 => "Oggi",
            1 => "Ieri",
            _ => local.ToString(local.Year == DateTimeOffset.Now.Year ? "d MMM" : "d MMM yyyy", c),
        };
        return $"{day}, {local.ToString("t", c)}";
    }
}
