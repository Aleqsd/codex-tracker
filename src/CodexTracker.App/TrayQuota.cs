using CodexTracker.Core;

namespace CodexTracker.App;

/// <summary>The quota drawn in the notification icon, chosen from the hover preview or the icon menu.</summary>
internal static class TrayQuota
{
    /// <summary>The chosen tool's active account, else the first active one; a plan without a 5-hour quota shows its week.</summary>
    internal static (AccountState? Account, ResetKind Window) Resolve(TrackerState state, TrackerPreferences preferences)
    {
        var account = state.ActiveAccounts.FirstOrDefault(a => a.Profile.Provider == preferences.TrayProvider) ?? state.ActiveAccount;
        return (account, preferences.TrayWindow == ResetKind.Short && account?.HasShortWindow != false ? ResetKind.Short : ResetKind.Weekly);
    }
}
