using System.Globalization;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.App;

/// <summary>
/// Display-ready summary for the Windows 11 widget (src/CodexTracker.Widgets, Store edition): the tracker stays the only
/// owner of its data and writes `widget.json` in its data folder; the widget only reads it. Texts are already localized.
/// Nothing secret: account display names, percentages and reset times, as on the tray tooltip.
/// Sizes: small = ring of the most urgent account in use, medium = every tracked account with the advice, large = the week.
/// </summary>
internal static class WidgetFeed
{
    internal const string FileName = "widget.json";
    private static string? _last;

    internal static void Write(string directory, TrackerState state, TrackerPreferences preferences, DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(Build(state, preferences, now));
        if (json == _last) return;
        var path = Path.Combine(directory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
        _last = json;
    }

    internal static object Build(TrackerState state, TrackerPreferences preferences, DateTimeOffset now)
    {
        state = state with { ManualCodexReset = preferences.ManualCodexReset };
        var icons = TrayQuota.Icons(state, preferences);
        string Name(AccountState a) => PrivacyText.Account(a.Profile, state, preferences);
        static string Color(double? remaining) => remaining switch { null => "Default", <= 10 => "Attention", <= 20 => "Warning", _ => "Default" };
        static string Percent(double? remaining) => remaining is { } r ? $"{Math.Floor(r):0} %" : "--";

        // Accounts in use: the window drawn in the icon, its refill and its colour.
        var active = state.ActiveAccounts.Select(account =>
        {
            var window = icons.FirstOrDefault(i => i.Account == account).Window == ResetKind.Short && account.HasShortWindow ? ResetKind.Short : ResetKind.Weekly;
            var remaining = QuotaPresentation.Remaining(state, account, window, now);
            var (at, projected) = QuotaPresentation.ShownResetsAt(state, account, window, now);
            var label = window == ResetKind.Short ? Loc.T("5 heures") : Loc.T("Semaine");
            return new
            {
                name = Name(account), provider = account.Profile.ProviderName, percent = Percent(remaining), value = remaining,
                color = Color(remaining), at,
                detail = at is { } reset ? Loc.F("{0} · recharge {1}", label, Display.ResetHour(reset).ToLowerInvariant()) + (projected ? " ≈" : "") : label,
                reset = at is { } r2 ? Display.ResetHour(r2) : ""
            };
        }).OrderBy(a => a.value ?? 101).ToArray();

        // Every tracked account, weekly view: a passed reset counts as probably refilled.
        var tracked = state.Accounts.Where(a => a.Snapshot is not null).Select(account =>
        {
            var available = AccountSuggestions.Available(state, account, now);
            return new
            {
                name = Name(account), provider = account.Profile.ProviderName == "Claude Code" ? "Claude" : account.Profile.ProviderName,
                percent = Percent(available?.Remaining) + (available?.Estimated == true ? " ≈" : ""), value = available?.Remaining,
                color = available?.Estimated == true ? "Default" : Color(available?.Remaining), active = account.IsActive
            };
        }).OrderBy(a => a.value ?? 101).ToArray();

        // The coming week: a day is marked when an account's 5-hour or weekly refill falls on it (accent for an account in use).
        var resets = ResetSchedule.Entries(state, now: now).Where(e => e.Kind is ResetKind.Weekly && e.At is { } at && at > now && at <= now.AddDays(7)).ToArray();
        var culture = Loc.Culture;
        var week = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = now.ToLocalTime().Date.AddDays(offset);
            var here = resets.Where(e => e.At!.Value.ToLocalTime().Date == day).ToArray();
            return new { day = day.ToString("ddd", culture).TrimEnd('.'), level = here.Any(e => e.Account.IsActive) ? 2 : here.Length > 0 ? 1 : 0 };
        }).ToArray();
        var next = active.Where(a => a.at is { } at && at > now).OrderBy(a => a.at).FirstOrDefault();

        var credits = state.Accounts.Where(a => a.Profile.Provider == AccountProvider.Codex && a.Snapshot is { AvailableResetCredits: > 0 })
            .SelectMany(a => a.Snapshot!.ResetCredits ?? []).Where(c => c.ExpiresAt > now).OrderBy(c => c.ExpiresAt).ToArray();
        var held = state.Accounts.Where(a => a.Profile.Provider == AccountProvider.Codex).Sum(a => a.Snapshot?.AvailableResetCredits ?? 0);
        var advice = state.ActiveAccounts.Select(a => NotificationPolicy.Suggestion(state, a, preferences, now)).FirstOrDefault(s => s is not null);

        return new
        {
            title = "Codex Tracker",
            accounts = active.Select(a => new { a.name, a.provider, a.percent, a.detail, a.color }),
            active = active.Select(a => new { a.name, a.provider, a.percent, value = a.value is { } v ? Math.Floor(v) : (double?)null, a.color, a.reset }),
            tracked = tracked.Select(a => new { a.name, a.provider, a.percent, value = a.value is { } v ? Math.Floor(v) : (double?)null, a.color, a.active }),
            next = next is null ? null : new
            {
                label = Loc.T("Prochaine recharge"),
                countdown = Display.Remaining(next.at),
                detail = Loc.F("{0} · {1} · il reste {2}", next.provider, next.reset.ToLowerInvariant(), next.percent)
            },
            week,
            reserve = credits.FirstOrDefault() is { ExpiresAt: { } expires } ? Loc.F("Réserve à utiliser avant {0}", Display.ResetHour(expires).ToLowerInvariant()) : "",
            reserves = held switch { 0 => "", 1 => Loc.T("1 réserve"), _ => Loc.F("{0} réserves", held.ToString(CultureInfo.InvariantCulture)) },
            empty = active.Length == 0 ? Loc.T("Ouvrez Codex ou Claude Code : le compte connecté apparaîtra ici.") : "",
            advice = advice ?? "",
            hasAdvice = advice is not null,
            openLabel = Loc.T("Ouvrir"),
            refreshLabel = Loc.T("Actualiser"),
            updated = Loc.F("Relevé {0:HH:mm}", (state.ActiveAccount?.Snapshot?.FetchedAt ?? now).ToLocalTime()),
        };
    }
}
