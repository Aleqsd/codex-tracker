namespace CodexTracker.Core;

public enum ReminderChannel { Windows, Sms, Call, Email }
public enum DeliveryStatus { Deferred, Submitting, Shown, Accepted, Delivered, Completed, Failed, Unknown, Skipped, Cancelled }
public sealed record ReminderRule(ResetKind Kind, bool Enabled, int[] LeadMinutes, ReminderChannel[] Channels, Guid[]? AccountIds = null);
public sealed record PhonePolicy(int SmsPerDay = 5, int CallsPerDay = 1, bool QuietEnabled = true,
    int QuietStart = 22, int QuietEnd = 8, string TimeZoneId = "Europe/Paris");
public sealed record ReminderOccurrence(string Key, Guid AccountId, string AccountName, ResetKind Kind,
    string? CreditId, DateTimeOffset At, DateTimeOffset ObservedAt, int LeadMinutes, ReminderChannel Channel, string? SourceUrl = null,
    AccountProvider Provider = AccountProvider.Codex)
{
    public string ProviderName => Provider == AccountProvider.ClaudeCode ? "Claude Code" : "Codex";
}
public sealed record ReminderDelivery(ReminderOccurrence Occurrence, DeliveryStatus Status, DateTimeOffset UpdatedAt,
    string Detail, string? ProviderId = null, DateTimeOffset? AttemptedAt = null, bool IsTest = false);
public sealed record DeliveryResult(DeliveryStatus Status, string Detail, string? ProviderId = null);

public static class ReminderPlanner
{
    public static readonly int[] AllowedMinutes = [30, 60, 360, 720, 1440, 4320, 10080];
    /// <summary>A reserve reset is lost once expired: several reminders, whatever the account's quota.</summary>
    public static readonly int[] ReserveLeadMinutes = [4320, 1440, 360, 60];
    public static ReminderRule[] Defaults(bool expiry = true, int legacyHours = 24) =>
    [new(ResetKind.Weekly, true, [1440, 60], [ReminderChannel.Windows]),
     new(ResetKind.Short, false, [60], [ReminderChannel.Windows]),
     new(ResetKind.Reserve, expiry, [.. ReserveLeadMinutes.Union([legacyHours * 60])], [ReminderChannel.Windows])];

    /// <summary>Untouched former reserve defaults (24 h and 1 h, Windows, every account) gain the 3-day and 6-hour reminders.</summary>
    public static ReminderRule[] UpgradeReserveDefaults(IEnumerable<ReminderRule> rules)
    {
        var normalized = Normalize(rules);
        var reserve = normalized.Where(r => r.Kind == ResetKind.Reserve).ToArray();
        var formerDefaults = reserve.Length == 2 && reserve.Select(r => r.LeadMinutes[0]).Order().SequenceEqual([60, 1440]) &&
            reserve.All(r => r.AccountIds is null && r.Channels.SequenceEqual([ReminderChannel.Windows]) && r.Enabled == reserve[0].Enabled);
        return formerDefaults
            ? [.. normalized.Where(r => r.Kind != ResetKind.Reserve), .. ReserveLeadMinutes.Select(m => reserve[0] with { LeadMinutes = [m] })]
            : normalized;
    }

    public static ReminderRule[] Normalize(IEnumerable<ReminderRule> rules) => rules.Where(r => r is not null && Enum.IsDefined(r.Kind))
        .SelectMany(r => (r.LeadMinutes ?? []).Where(m => AllowedMinutes.Contains(m) && (r.Kind != ResetKind.Short || m < 300)).Distinct().Select(m => r with { LeadMinutes = [m] }))
        .GroupBy(r => (r.Kind, r.LeadMinutes[0])).Select(g => g.Last()).Select(r => r with {
            Channels = (r.Channels ?? []).Where(Enum.IsDefined).Distinct().ToArray(), AccountIds = r.AccountIds?.Distinct().ToArray()
        }).ToArray();

    // Return all crossed offsets: the caller records older offsets as skipped, so they never reappear.
    public static IReadOnlyList<ReminderOccurrence> Due(TrackerState state, IEnumerable<ReminderRule> rules, DateTimeOffset now)
    {
        var result = new List<ReminderOccurrence>();
        foreach (var entry in ResetSchedule.Entries(state, now: now))
        {
            if (entry.At is not { } at || at <= now || entry.Account.Snapshot is not { } snapshot || snapshot.FetchedAt > now) continue;
            if (GlobalResetAnnouncement.For(entry.Account, entry.Kind, state.GlobalResetFeed, now) is not null) continue;
            if (entry.Kind == ResetKind.Reserve && (snapshot.AvailableResetCredits is not > 0 || string.IsNullOrWhiteSpace(entry.CreditId))) continue;
            foreach (var rule in Normalize(rules).Where(r => r.Enabled && r.Kind == entry.Kind && (r.AccountIds is null || r.AccountIds.Contains(entry.Account.Profile.Id))))
            foreach (var lead in rule.LeadMinutes.Where(m => at.AddMinutes(-m) <= now))
            foreach (var channel in rule.Channels)
            {
                var identity = CalendarExport.Identity(entry.Account.Profile.Id, entry.Kind.ToString(), entry.CreditId ?? "quota", at);
                result.Add(new($"{identity}/{lead}/{channel}", entry.Account.Profile.Id, entry.Account.Profile.Email,
                    entry.Kind, entry.CreditId, at, snapshot.FetchedAt, lead, channel, Provider: entry.Account.Profile.Provider));
            }
        }
        return result.DistinctBy(r => r.Key).OrderBy(r => r.At).ThenBy(r => r.LeadMinutes).ToArray();
    }
    public static string EventKey(ReminderOccurrence r) => $"{r.AccountId}/{r.Kind}/{r.CreditId}/{r.At.UtcTicks}/{r.Channel}";
    public static string Label(ResetKind kind) => kind switch { ResetKind.Weekly => Loc.T("Reset hebdomadaire"), ResetKind.Short => Loc.T("Reset 5 heures"), _ => Loc.T("Expiration de réserve") };
    public static bool IsGlobalReset(ReminderOccurrence r) => r.LeadMinutes == 0 && r.Key.StartsWith("global/", StringComparison.Ordinal);
    public static bool IsExpectedReset(ReminderOccurrence r) => r.LeadMinutes == 0 && (r.Key.StartsWith("expected/", StringComparison.Ordinal) || IsGlobalReset(r));
    public static string Title(ReminderOccurrence r) => IsGlobalReset(r) ? Loc.T("Reset général annoncé comme terminé") : IsExpectedReset(r) ? Loc.T("Compte probablement rechargé")
        : r.Kind == ResetKind.Reserve ? Loc.T("Un reset en réserve va expirer") : Label(r.Kind);
    public static string Body(ReminderOccurrence r) => IsGlobalReset(r)
        ? r.Kind == ResetKind.Weekly
            ? Loc.F("{0} · Semaine probablement à 100 %.\nConfirmation publique du {1:dd/MM/yyyy HH:mm:ss zzz}.\nDernier relevé : {2:dd/MM/yyyy HH:mm:ss zzz}. À confirmer dans {3} ; source dans Resets.", r.AccountName, r.At.ToLocalTime(), r.ObservedAt.ToLocalTime(), r.ProviderName)
            : Loc.F("{0} · 5 heures probablement à 100 %.\nConfirmation publique du {1:dd/MM/yyyy HH:mm:ss zzz}.\nDernier relevé : {2:dd/MM/yyyy HH:mm:ss zzz}. À confirmer dans {3} ; source dans Resets.", r.AccountName, r.At.ToLocalTime(), r.ObservedAt.ToLocalTime(), r.ProviderName)
        : IsExpectedReset(r)
        ? r.Kind == ResetKind.Weekly
            ? Loc.F("{0} · Semaine probablement à 100 %.\nReset prévu le {1:dd/MM/yyyy HH:mm:ss zzz}.\nDernier relevé : {2:dd/MM/yyyy HH:mm:ss zzz}. À confirmer dans {3}.", r.AccountName, r.At.ToLocalTime(), r.ObservedAt.ToLocalTime(), r.ProviderName)
            : Loc.F("{0} · 5 heures probablement à 100 %.\nReset prévu le {1:dd/MM/yyyy HH:mm:ss zzz}.\nDernier relevé : {2:dd/MM/yyyy HH:mm:ss zzz}. À confirmer dans {3}.", r.AccountName, r.At.ToLocalTime(), r.ObservedAt.ToLocalTime(), r.ProviderName)
        : r.Kind == ResetKind.Reserve
        ? Loc.F("{0} · expire le {1:dd/MM/yyyy HH:mm zzz}.\nUtilisez-le dans {2} avant cette date pour ne pas le perdre.\nRelevé : {3:dd/MM/yyyy HH:mm zzz}.", r.AccountName, r.At.ToLocalTime(), r.ProviderName, r.ObservedAt.ToLocalTime())
        : Loc.F("{0} · {1}\nÉchéance : {2:dd/MM/yyyy HH:mm:ss zzz}\nRelevé : {3:dd/MM/yyyy HH:mm:ss zzz}\nÀ confirmer dans {4}.", r.AccountName, Label(r.Kind), r.At.ToLocalTime(), r.ObservedAt.ToLocalTime(), r.ProviderName);
    public static TimeZoneInfo Zone(PhonePolicy policy) => TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
    public static bool IsQuiet(DateTimeOffset now, PhonePolicy policy)
    {
        if (!policy.QuietEnabled || policy.QuietStart == policy.QuietEnd) return false;
        var hour = TimeZoneInfo.ConvertTime(now, Zone(policy)).Hour;
        return policy.QuietStart < policy.QuietEnd ? hour >= policy.QuietStart && hour < policy.QuietEnd : hour >= policy.QuietStart || hour < policy.QuietEnd;
    }
    public static bool AtDailyLimit(ReminderChannel channel, DateTimeOffset now, PhonePolicy policy, IEnumerable<ReminderDelivery> history)
    {
        if (channel is not (ReminderChannel.Sms or ReminderChannel.Call)) return false;
        var zone = Zone(policy); var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        var count = history.Count(d => d.Occurrence.Channel == channel && d.AttemptedAt is { } at && TimeZoneInfo.ConvertTime(at, zone).Date == date);
        return count >= (channel == ReminderChannel.Sms ? policy.SmsPerDay : policy.CallsPerDay);
    }
}
