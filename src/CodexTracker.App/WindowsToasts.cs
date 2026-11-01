using CommunityToolkit.WinUI.Notifications;

namespace CodexTracker.App;

/// <summary>Windows notifications with actions. Windows itself handles snoozing; the tracker only opens a page.</summary>
internal static class WindowsToasts
{
    private const string PageArgument = "page";
    private const string CalendarArgument = "calendar";
    private const string ActionArgument = "action";
    internal const string SyncAction = "sync", DismissSyncAction = "nosuggest";

    /// <param name="calendar">Calendar identity of a reset credit: adds an "add to Google Calendar" action.</param>
    /// <param name="suggestSync">Offers to connect Google Calendar instead of the usual open and snooze actions.</param>
    internal static void Show(string title, string body, string page, string? calendar = null, bool suggestSync = false)
    {
        if (suggestSync && calendar is not null)
        {
            new ToastContentBuilder()
                .AddArgument(PageArgument, page)
                .AddText(title).AddText(body)
                .AddButton(new ToastButton().SetContent(Loc.T("Synchroniser l’agenda")).AddArgument(ActionArgument, SyncAction))
                .AddButton(new ToastButton().SetContent(Loc.T("Ajouter cet événement")).AddArgument(PageArgument, page).AddArgument(CalendarArgument, calendar))
                .AddButton(new ToastButton().SetContent(Loc.T("Ne plus proposer")).AddArgument(ActionArgument, DismissSyncAction).SetBackgroundActivation())
                .Show(toast => toast.Group = "codex-tracker");
            return;
        }
        var builder = new ToastContentBuilder()
            .AddArgument(PageArgument, page)
            .AddText(title).AddText(body)
            .AddToastInput(new ToastSelectionBox("snooze")
            {
                DefaultSelectionBoxItemId = "60",
                Items = { new("15", Loc.T("Dans 15 minutes")), new("60", Loc.T("Dans 1 heure")), new("240", Loc.T("Dans 4 heures")) }
            });
        if (calendar is not null)
            builder.AddButton(new ToastButton().SetContent(Loc.T("Ajouter à Google Agenda")).AddArgument(PageArgument, page).AddArgument(CalendarArgument, calendar));
        builder.AddButton(new ToastButton().SetContent(Loc.T("Ouvrir le suivi")).AddArgument(PageArgument, page))
            .AddButton(new ToastButtonSnooze(Loc.T("Rappeler")) { SelectionBoxId = "snooze" })
            .Show(toast => toast.Group = "codex-tracker");
    }

    /// <summary>Clicks arrive on a background thread, also when Windows starts the tracker for them.</summary>
    internal static void Listen(Action<string, string?, string?> open) => ToastNotificationManagerCompat.OnActivated += activation =>
    {
        var arguments = ToastArguments.Parse(activation.Argument);
        var action = arguments.TryGetValue(ActionArgument, out var chosen) && chosen is SyncAction or DismissSyncAction ? chosen : null;
        var page = arguments.TryGetValue(PageArgument, out var value) && value is "Comptes" or "Resets" ? value : "Comptes";
        // Only a calendar identity is accepted (64 hex characters); anything else just opens the page.
        var calendar = arguments.TryGetValue(CalendarArgument, out var key) && key is { Length: 64 } && key.All(char.IsAsciiHexDigitLower) ? key : null;
        open(page, calendar, action);
    };

    /// <summary>Removes the per-user notification registration; called by the uninstaller.</summary>
    internal static void Unregister() => ToastNotificationManagerCompat.Uninstall();
}
