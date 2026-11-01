using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.App;

/// <summary>
/// Display-ready summary for the Windows 11 widget (src/CodexTracker.Widgets, Store edition): the tracker stays the only
/// owner of its data and writes `widget.json` in its data folder; the widget only reads it. Texts are already localized.
/// Nothing secret: account display names, percentages and reset times, as on the tray tooltip.
/// </summary>
internal static class WidgetFeed
{
    internal const string FileName = "widget.json";
    private static string? _last;

    internal static void Write(string directory, TrackerState state, TrackerPreferences preferences, DateTimeOffset now)
    {
        state = state with { ManualCodexReset = preferences.ManualCodexReset };
        var icons = TrayQuota.Icons(state, preferences);
        var accounts = state.ActiveAccounts.Select(account =>
        {
            var window = icons.FirstOrDefault(i => i.Account == account).Window == ResetKind.Short && account.HasShortWindow ? ResetKind.Short : ResetKind.Weekly;
            var remaining = QuotaPresentation.Remaining(state, account, window, now);
            var (at, projected) = QuotaPresentation.ShownResetsAt(state, account, window, now);
            var label = window == ResetKind.Short ? Loc.T("5 heures") : Loc.T("Semaine");
            var detail = at is { } reset ? Loc.F("{0} · recharge {1}", label, Display.ResetHour(reset).ToLowerInvariant()) + (projected ? " ≈" : "") : label;
            return new
            {
                name = PrivacyText.Account(account.Profile, state, preferences),
                provider = account.Profile.ProviderName,
                percent = remaining is { } r ? $"{Math.Floor(r):0} %" : "--",
                detail,
                color = remaining switch { <= 10 => "Attention", <= 20 => "Warning", _ => "Default" }
            };
        }).ToArray();
        var advice = state.ActiveAccounts.Select(a => NotificationPolicy.Suggestion(state, a, preferences, now)).FirstOrDefault(s => s is not null);
        var feed = new
        {
            title = "Codex Tracker",
            accounts,
            empty = accounts.Length == 0 ? Loc.T("Ouvrez Codex ou Claude Code : le compte connecté apparaîtra ici.") : "",
            advice = advice ?? "",
            hasAdvice = advice is not null,
            openLabel = Loc.T("Ouvrir le suivi"),
            updated = Loc.F("Relevé {0:HH:mm}", (state.ActiveAccount?.Snapshot?.FetchedAt ?? now).ToLocalTime()),
        };
        var json = JsonSerializer.Serialize(feed);
        if (json == _last) return;
        var path = Path.Combine(directory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
        _last = json;
    }
}
