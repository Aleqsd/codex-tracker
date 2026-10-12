using CodexTracker.Core;

namespace CodexTracker.App;

/// <summary>The quotas drawn in the notification area, chosen from the hover preview or the icon menu.</summary>
internal static class TrayQuota
{
    /// <summary>Window last chosen for a tool; files from 0.9.26 only know the one of the chosen tool.</summary>
    internal static ResetKind WindowFor(TrackerPreferences preferences, AccountProvider tool) =>
        preferences.TrayWindows.TryGetValue(tool, out var window) ? window : tool == preferences.TrayProvider ? preferences.TrayWindow : ResetKind.Weekly;

    /// <summary>The single icon: the chosen tool's active account, else the first active one, with that tool's window.</summary>
    internal static (AccountState? Account, ResetKind Window) Resolve(TrackerState state, TrackerPreferences preferences)
    {
        var account = state.ActiveAccounts.FirstOrDefault(a => a.Profile.Provider == preferences.TrayProvider) ?? state.ActiveAccount;
        return (account, Shown(account, WindowFor(preferences, account?.Profile.Provider ?? preferences.TrayProvider)));
    }

    /// <summary>One quota per tool in use when each tool has its icon, otherwise the single choice.</summary>
    internal static IReadOnlyList<(AccountState? Account, ResetKind Window)> Icons(TrackerState state, TrackerPreferences preferences) =>
        preferences.TrayIconPerTool && state.ActiveAccounts.Count > 1
            ? [.. state.ActiveAccounts.Select(account => ((AccountState?)account, Shown(account, WindowFor(preferences, account.Profile.Provider))))]
            : [Resolve(state, preferences)];

    /// <summary>A plan without a 5-hour quota (Codex Pro) shows its week.</summary>
    private static ResetKind Shown(AccountState? account, ResetKind window) =>
        window == ResetKind.Short && account?.HasShortWindow != false ? ResetKind.Short : ResetKind.Weekly;
}
