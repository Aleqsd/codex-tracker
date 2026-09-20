using CodexTracker.Core;

namespace CodexTracker.App;

internal static class NotificationPolicy
{
    public static bool IsEnabled(QuotaNotification notification, TrackerPreferences preferences) =>
        notification.Kind == NotificationKind.Reset ? preferences.ResetNotifications : notification.Threshold switch
        {
            20 => preferences.Alert20,
            10 => preferences.Alert10,
            5 => preferences.Alert5,
            _ => false
        };

    public static (string Title, string Body) Compose(QuotaNotification notification, TrackerState state)
    {
        var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == notification.AccountId);
        // Windows may retain a notification after privacy mode changes. Never send an email to its history.
        var name = account is null ? "Compte suivi" : PrivacyText.Account(account.Profile, state, true);
        var window = notification.Window == UsageWindowKind.Short ? "5 heures" : "semaine";
        return notification.Kind == NotificationKind.Reset
            ? ("Quota rechargé", $"{name} · {window}\nCodex a confirmé le renouvellement du quota.")
            : ("Quota bientôt épuisé", $"{name} · {window}\nIl reste {notification.Threshold} % ou moins. Cliquez pour consulter le suivi.");
    }
}
