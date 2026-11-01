using CodexTracker.App.Updates;

namespace CodexTracker.App.Mcp;

/// <summary>
/// What the dashboard, the details window and Settings show, projected for assistants. Each notice carries the same keys
/// as the dashboard banner, so dismissing it here hides it there.
/// </summary>
internal static class AgentQueries
{
    internal sealed record Notice(string Kind, string[] Keys, Guid[] AccountIds, string Text, bool Dismissed);

    internal static IReadOnlyList<Notice> Notices(TrackerState state, TrackerPreferences preferences, DateTimeOffset now)
    {
        var notices = new List<Notice>();
        if (preferences.ManualCodexReset is { } manual)
        {
            var covered = state.Accounts.Where(a => manual.Applies(a, ResetKind.Weekly, now) || manual.Applies(a, ResetKind.Short, now)).Select(a => a.Profile.Id).ToArray();
            if (covered.Length > 0)
                notices.Add(new("manualReset", [manual.DeclaredAt.ToString("O")], covered,
                    Loc.F("Reset Codex déclaré le {0} · {1} compte(s) en attente d’un nouveau relevé", Display.Exact(manual.At), covered.Length),
                    preferences.DismissedManualCodexReset == manual.DeclaredAt));
        }
        var expected = state.Accounts.SelectMany(a => new[] { ResetKind.Weekly, ResetKind.Short }
                .Select(kind => ExpectedReset.For(a, kind, now, state.GlobalResetFeed, preferences.ManualCodexReset))
                .OfType<ExpectedReset>()
                .Select(r => (Account: a.Profile.Id, Key: r.Announcement is { } announcement ? $"global/{announcement.Id}/{a.Profile.Id}/{r.Kind}" : $"expected/{a.Profile.Id}/{r.Kind}/{r.At:O}")))
            .ToArray();
        if (expected.Length > 0)
        {
            var accounts = expected.Select(e => e.Account).Distinct().ToArray();
            notices.Add(new("expectedResets", [.. expected.Select(e => e.Key)], accounts,
                accounts.Length == 1 ? Loc.T("Un autre compte est probablement rechargé") : Loc.F("{0} autres comptes sont probablement rechargés", accounts.Length),
                expected.All(e => preferences.DismissedNotices.Contains(e.Key))));
        }
        var advice = AccountAdvisor.Evaluate(state, now);
        if (advice.Kind == AccountAdviceKind.VerifyInCodex && advice.AccountId is { } id && state.Accounts.FirstOrDefault(a => a.Profile.Id == id) is { } account)
        {
            var key = $"advice/{id}/{advice.ObservedAt:O}";
            notices.Add(new("advice", [key], [id], Loc.F("À vérifier dans Codex : {0}", PrivacyText.Account(account.Profile, state, preferences)) + " · " + advice.Reason,
                preferences.DismissedNotices.Contains(key)));
        }
        return notices;
    }

    /// <summary>The dismissed keys after hiding these notices; keys of notices no longer shown are dropped, like the dashboard.</summary>
    internal static string[] Dismissing(TrackerPreferences preferences, IReadOnlyList<Notice> current, IEnumerable<string> keys) =>
        preferences.DismissedNotices.Intersect(current.Where(n => n.Kind != "manualReset").SelectMany(n => n.Keys)).Union(keys).ToArray();

    internal static object? Forecast(ITrackerService service, AccountState account)
    {
        if (!account.IsActive) return null;
        var forecast = service.GetForecast(account.Profile.Id);
        return new { window = forecast.Window?.ToString(), forecast.EstimatedExhaustionAt, forecast.LastsUntilReset, explanation = forecast.Explanation };
    }

    internal static object? Pace(TrackerState state, AccountState account, DateTimeOffset now)
    {
        if (!account.IsActive) return null;
        object? For(ResetKind kind) => QuotaPace.For(state, account, kind, now) is { } pace
            ? new { remaining = pace.Remaining, expected = pace.Expected, margin = pace.Margin, pace.OnPace, left = pace.Left.ToString() } : null;
        return new { weekly = For(ResetKind.Weekly), shortWindow = account.HasShortWindow ? For(ResetKind.Short) : null };
    }

    /// <summary>Usage samples of one window over the last hours, thinned to at most 400 points.</summary>
    internal static object History(ITrackerService service, Guid accountId, int hours, ResetKind window, DateTimeOffset now)
    {
        var since = now.AddHours(-hours);
        var points = service.GetHistory(accountId).Where(s => s.Timestamp >= since)
            .Select(s => (s.Timestamp, Remaining: window == ResetKind.Short ? s.ShortRemaining : s.WeeklyRemaining, ResetsAt: window == ResetKind.Short ? s.ShortResetsAt : s.WeeklyResetsAt))
            .Where(p => p.Remaining is not null).ToArray();
        var step = Math.Max(1, (int)Math.Ceiling(points.Length / 400.0));
        return new
        {
            accountId, hours, window = window.ToString(), count = points.Length,
            points = points.Where((_, i) => i % step == 0 || i == points.Length - 1).Select(p => new { at = p.Timestamp, remaining = p.Remaining, resetsAt = p.ResetsAt })
        };
    }

    internal static object GlobalFeed(TrackerState state, DateTimeOffset now)
    {
        var feed = state.GlobalResetFeed;
        return new
        {
            available = feed is not null, feed?.CheckedAt, feed?.Error, feed?.IsChecking, feed?.NextCheckAt, feed?.ManualRetryAt,
            announcements = (feed?.Announcements ?? []).Select(a => new
            {
                a.Id, a.Author, a.SourceUrl, a.AnnouncementUrl, a.AnnouncedAt, a.VerifiedAt, a.Plans, kinds = a.Kinds.Select(k => k.ToString()),
                accounts = state.Accounts.Where(x => x.Profile.Provider == AccountProvider.Codex).SelectMany(x => a.Kinds.Select(k => new
                { accountId = x.Profile.Id, kind = k.ToString(), status = a.StatusFor(x, k, now).ToString() }))
            })
        };
    }

    internal static object Updates(UpdateService updates)
    {
        var check = updates.ReadLatestCheck();
        return new
        {
            current = updates.CurrentVersion, store = AppPackage.IsPackaged, selfUpdating = UpdateService.CanSelfUpdate, updates.IncludePrereleases,
            lastResult = updates.ReadLastResult(),
            latestCheck = check is null ? null : new { available = check.Release?.Version, source = check.Source.ToString(), verified = check.IsVerifiedNow, notes = check.Release?.NotesUrl },
            prepared = updates.Prepared?.Release.Version, updates.IsPreparing, updates.PreparationMessage,
            releases = UpdateService.ReleasesPage
        };
    }

    internal static object ClaudeCodeSetup(PreferencesStore preferences, bool demo)
    {
        var location = Codex.ClaudeCodeLocation.Resolve(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var status = demo ? null : new Codex.ClaudeCodeObservations(preferences.DataDirectory, location).ReadStatus(AppPackage.LaunchPath);
        return new
        {
            configuration = Codex.ClaudeCodeObservations.Configuration(AppPackage.LaunchPath),
            configPath = location.ConfigPath, configured = status?.Configured, lastReading = status?.LastReading,
            stale = status?.LastReading is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromDays(7)
        };
    }

    /// <summary>The calendar entries the "other calendars" window exports, optionally for one account.</summary>
    internal static IReadOnlyList<CalendarEntry> CalendarEntries(TrackerState state, TrackerPreferences preferences, Guid? accountId, DateTimeOffset now)
    {
        var scope = accountId is { } id ? state with { Accounts = [.. state.Accounts.Where(a => a.Profile.Id == id)] } : state;
        return CalendarExport.Entries(scope, p => PrivacyText.Account(p, state, preferences), now);
    }
}
