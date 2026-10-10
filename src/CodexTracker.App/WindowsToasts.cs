using CommunityToolkit.WinUI.Notifications;

namespace CodexTracker.App;

/// <summary>Windows notifications with actions. Windows itself handles snoozing; the tracker only opens a page.</summary>
internal static class WindowsToasts
{
    private const string PageArgument = "page";

    internal static void Show(string title, string body, string page)
    {
        new ToastContentBuilder()
            .AddArgument(PageArgument, page)
            .AddText(title).AddText(body)
            .AddToastInput(new ToastSelectionBox("snooze")
            {
                DefaultSelectionBoxItemId = "60",
                Items = { new("15", Loc.T("Dans 15 minutes")), new("60", Loc.T("Dans 1 heure")), new("240", Loc.T("Dans 4 heures")) }
            })
            .AddButton(new ToastButton().SetContent(Loc.T("Ouvrir le suivi")).AddArgument(PageArgument, page))
            .AddButton(new ToastButtonSnooze(Loc.T("Rappeler")) { SelectionBoxId = "snooze" })
            .Show(toast => toast.Group = "codex-tracker");
    }

    /// <summary>Clicks arrive on a background thread, also when Windows starts the tracker for them.</summary>
    internal static void Listen(Action<string> open) => ToastNotificationManagerCompat.OnActivated += activation =>
        open(ToastArguments.Parse(activation.Argument).TryGetValue(PageArgument, out var page) && page is "Comptes" or "Resets" ? page : "Comptes");

    /// <summary>Removes the per-user notification registration; called by the uninstaller.</summary>
    internal static void Unregister() => ToastNotificationManagerCompat.Uninstall();
}
