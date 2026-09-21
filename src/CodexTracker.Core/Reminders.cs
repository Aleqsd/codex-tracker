namespace CodexTracker.Core;

public enum ReminderChannel { Windows, Sms, Call, Email }
public enum DeliveryStatus { Deferred, Submitting, Shown, Accepted, Delivered, Completed, Failed, Unknown, Skipped, Cancelled }
public sealed record ReminderRule(ResetKind Kind, bool Enabled, int[] LeadMinutes, ReminderChannel[] Channels, Guid[]? AccountIds = null);
public sealed record PhonePolicy(int SmsPerDay = 5, int CallsPerDay = 1, bool QuietEnabled = true,
    int QuietStart = 22, int QuietEnd = 8, string TimeZoneId = "Europe/Paris");
public sealed record ReminderOccurrence(string Key, Guid AccountId, string AccountName, ResetKind Kind,
    string? CreditId, DateTimeOffset At, DateTimeOffset ObservedAt, int LeadMinutes, ReminderChannel Channel);
public sealed record ReminderDelivery(ReminderOccurrence Occurrence, DeliveryStatus Status, DateTimeOffset UpdatedAt,
    string Detail, string? ProviderId = null, DateTimeOffset? AttemptedAt = null, bool IsTest = false);
public sealed record DeliveryResult(DeliveryStatus Status, string Detail, string? ProviderId = null);

public static class ReminderPlanner
{
    public static readonly int[] AllowedMinutes = [30, 60, 360, 720, 1440, 4320, 10080];
    public static ReminderRule[] Defaults(bool expiry = true, int legacyHours = 24) =>
    [new(ResetKind.Weekly, true, [1440, 60], [ReminderChannel.Windows]),
     new(ResetKind.Short, false, [60], [ReminderChannel.Windows]),
     new(ResetKind.Reserve, expiry, [legacyHours * 60, 60], [ReminderChannel.Windows])];

    public static ReminderRule[] Normalize(IEnumerable<ReminderRule> rules) => rules.Where(r => r is not null && Enum.IsDefined(r.Kind))
        .SelectMany(r => (r.LeadMinutes ?? []).Where(m => AllowedMinutes.Contains(m) && (r.Kind != ResetKind.Short || m < 300)).Distinct().Select(m => r with { LeadMinutes = [m] }))
        .GroupBy(r => (r.Kind, r.LeadMinutes[0])).Select(g => g.Last()).Select(r => r with {
            Channels = (r.Channels ?? []).Where(Enum.IsDefined).Distinct().ToArray(), AccountIds = r.AccountIds?.Distinct().ToArray()
        }).ToArray();

    // Return all crossed offsets: the caller records older offsets as skipped, so they never reappear.
    public static IReadOnlyList<ReminderOccurrence> Due(TrackerState state, IEnumerable<ReminderRule> rules, DateTimeOffset now)
    {
        var result = new List<ReminderOccurrence>();
        foreach (var entry in ResetSchedule.Entries(state))
        {
            if (entry.At is not { } at || at <= now || entry.Account.Snapshot is not { } snapshot || snapshot.FetchedAt > now) continue;
            if (entry.Kind == ResetKind.Reserve && (snapshot.AvailableResetCredits is not > 0 || string.IsNullOrWhiteSpace(entry.CreditId))) continue;
            foreach (var rule in Normalize(rules).Where(r => r.Enabled && r.Kind == entry.Kind && (r.AccountIds is null || r.AccountIds.Contains(entry.Account.Profile.Id))))
            foreach (var lead in rule.LeadMinutes.Where(m => at.AddMinutes(-m) <= now))
            foreach (var channel in rule.Channels)
            {
                var identity = CalendarExport.Identity(entry.Account.Profile.Id, entry.Kind.ToString(), entry.CreditId ?? "quota", at);
                result.Add(new($"{identity}/{lead}/{channel}", entry.Account.Profile.Id, entry.Account.Profile.Email,
                    entry.Kind, entry.CreditId, at, snapshot.FetchedAt, lead, channel));
            }
        }
        return result.DistinctBy(r => r.Key).OrderBy(r => r.At).ThenBy(r => r.LeadMinutes).ToArray();
    }
    public static string EventKey(ReminderOccurrence r) => $"{r.AccountId}/{r.Kind}/{r.CreditId}/{r.At.UtcTicks}/{r.Channel}";
    public static string Label(ResetKind kind) => kind switch { ResetKind.Weekly => "Reset hebdomadaire", ResetKind.Short => "Reset 5 heures", _ => "Expiration de réserve" };
    public static string Body(ReminderOccurrence r) => $"{r.AccountName} · {Label(r.Kind)}\nÉchéance : {r.At.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}\nRelevé : {r.ObservedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}\nÀ confirmer dans Codex.";
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
