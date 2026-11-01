using CodexTracker.Core;

namespace CodexTracker.App;

internal static class NotificationPolicy
{
    public static bool IsEnabled(QuotaNotification notification, TrackerPreferences preferences) =>
        notification.Kind == NotificationKind.Reset ? preferences.ResetNotifications && (!preferences.SmartReminders || SmartReminders.Worth(notification.PreviousRemaining))
        : notification.Kind == NotificationKind.Forecast ? preferences.ForecastNotifications : notification.Threshold switch
        {
            20 => preferences.Alert20,
            10 => preferences.Alert10,
            5 => preferences.Alert5,
            _ => false
        };

    public static (string Title, string Body) Compose(QuotaNotification notification, TrackerState state, TrackerPreferences? preferences = null, DateTimeOffset? now = null)
    {
        var (title, body) = Text(notification, state, preferences);
        if (notification.Kind == NotificationKind.Reset) return (title, body);
        var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == notification.AccountId);
        return account is not null && Suggestion(state, account, preferences ?? new(), now ?? DateTimeOffset.UtcNow) is { } advice ? (title, body + "\n" + advice) : (title, body);
    }

    /// <summary>"Switch to X": the same tool's account with clearly more quota, when this one is nearly empty.</summary>
    public static string? Suggestion(TrackerState state, AccountState account, TrackerPreferences preferences, DateTimeOffset now)
    {
        if (AccountSuggestions.For(state, account, now) is not { } best) return null;
        var name = PrivacyText.Account(best.Account.Profile, state, preferences);
        return best.Estimated ? Loc.F("Passez sur {0} : probablement rechargé, à confirmer.", name)
            : Loc.F("Passez sur {0} : il lui reste {1:0} %.", name, Math.Floor(best.Remaining));
    }

    private static (string Title, string Body) Text(QuotaNotification notification, TrackerState state, TrackerPreferences? preferences)
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
