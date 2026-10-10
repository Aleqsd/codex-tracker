using CodexTracker.Core;

namespace CodexTracker.App;

internal static class NotificationPolicy
{
    public static bool IsEnabled(QuotaNotification notification, TrackerPreferences preferences) =>
        notification.Kind == NotificationKind.Reset ? preferences.ResetNotifications
        : notification.Kind == NotificationKind.Forecast ? preferences.ForecastNotifications : notification.Threshold switch
        {
            20 => preferences.Alert20,
            10 => preferences.Alert10,
            5 => preferences.Alert5,
            _ => false
        };

    public static (string Title, string Body) Compose(QuotaNotification notification, TrackerState state, TrackerPreferences? preferences = null)
    {
        var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == notification.AccountId);
        // Use the same display name as the account panel.
        var name = account is null ? Loc.T("Compte suivi") : PrivacyText.Account(account.Profile, state, preferences ?? new());
        var window = notification.Window == UsageWindowKind.Short ? Loc.T("5 heures") : Loc.T("semaine");
        var provider = account?.Profile.ProviderName ?? "Codex";
        if (notification.Kind == NotificationKind.Forecast)
            return (Loc.T("Quota bientôt épuisé à ce rythme"), Loc.F("{0} · {1}\nAu rythme actuel, plus rien vers {2:HH:mm}, avant la recharge.", name, window, notification.ExhaustionAt?.ToLocalTime()));
        return notification.Kind == NotificationKind.Reset
            ? (Loc.T("Quota rechargé"), Loc.F("{0} · {1}\n{2} a confirmé le renouvellement du quota.", name, window, provider))
            : (Loc.T("Quota bientôt épuisé"), Loc.F("{0} · {1}\nIl reste {2} % ou moins. Cliquez pour consulter le suivi.", name, window, notification.Threshold));
    }
}
