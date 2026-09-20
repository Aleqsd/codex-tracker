namespace CodexTracker.Core;

public enum UsageWindowKind { Weekly, Short }
public enum NotificationKind { Threshold, Reset }

public sealed record UsageSample(Guid AccountId, DateTimeOffset Timestamp, double? WeeklyRemaining,
    DateTimeOffset? WeeklyResetsAt, double? ShortRemaining = null, DateTimeOffset? ShortResetsAt = null);
public sealed record UsageForecast(TimeSpan? TimeToExhaustion, DateTimeOffset? EstimatedExhaustionAt,
    string Explanation, UsageWindowKind? Window = null);
public sealed record QuotaNotification(Guid AccountId, string Email, NotificationKind Kind, int? Threshold,
    DateTimeOffset ObservedAt, UsageWindowKind Window = UsageWindowKind.Weekly);

public static class UsageAnalytics
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public static readonly TimeSpan SampleCadence = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumObservationGap = TimeSpan.FromMinutes(5);
    public const int MaximumSamplesPerAccount = 64_802;

    public static UsageSample FromSnapshot(Guid accountId, AccountSnapshot snapshot) => new(accountId,
        snapshot.FetchedAt, snapshot.Weekly?.RemainingPercent, snapshot.Weekly?.ResetsAt,
        snapshot.Short?.RemainingPercent, snapshot.Short?.ResetsAt);

    /// <summary>Keeps one latest point per two-minute bucket, preserving an observed reset boundary.</summary>
    public static IReadOnlyList<UsageSample> Append(IReadOnlyList<UsageSample> history, UsageSample sample)
    {
        if (!IsValid(sample)) return history;
        if (history.Count > 0 && sample.Timestamp <= history[^1].Timestamp) return history;
        var retained = history.Where(p => p.AccountId == sample.AccountId && IsValid(p) &&
            p.Timestamp >= sample.Timestamp - Retention && p.Timestamp < sample.Timestamp).ToList();
        if (retained.Count > 0 && retained[^1].Timestamp.UtcTicks / SampleCadence.Ticks == sample.Timestamp.UtcTicks / SampleCadence.Ticks &&
            retained[^1].WeeklyResetsAt == sample.WeeklyResetsAt && retained[^1].ShortResetsAt == sample.ShortResetsAt)
            retained[^1] = sample;
        else retained.Add(sample);
        if (retained.Count > MaximumSamplesPerAccount) retained.RemoveRange(0, retained.Count - MaximumSamplesPerAccount);
        return retained.AsReadOnly();
    }

    public static UsageForecast Estimate(IReadOnlyList<UsageSample> history, DateTimeOffset now)
    {
        if (history.Count == 0) return Unavailable("L'estimation apparaîtra après au moins 15 minutes de suivi continu.");
        var ordered = history.Where(IsValid).OrderBy(p => p.Timestamp).ToArray();
        if (ordered.Length == 0 || now - ordered[^1].Timestamp > MaximumObservationGap || ordered[^1].Timestamp > now + TimeSpan.FromMinutes(1))
            return Unavailable("Le dernier relevé est trop ancien pour estimer la consommation actuelle.");
        var weekly = EstimateWindow(ordered, now, UsageWindowKind.Weekly);
        var shortWindow = EstimateWindow(ordered, now, UsageWindowKind.Short);
        var valid = new[] { weekly, shortWindow }.Where(f => f.EstimatedExhaustionAt is not null)
            .OrderBy(f => f.EstimatedExhaustionAt).FirstOrDefault();
        if (valid is not null) return valid;
        return ordered[^1].WeeklyRemaining is not null ? weekly : shortWindow;
    }

    private static UsageForecast EstimateWindow(UsageSample[] history, DateTimeOffset now, UsageWindowKind window)
    {
        var latest = history[^1];
        var remaining = Remaining(latest, window);
        var reset = Reset(latest, window);
        var label = window == UsageWindowKind.Weekly ? "hebdomadaire" : "de 5 heures";
        if (remaining is null || reset is null) return Unavailable($"Les informations du quota {label} sont insuffisantes.", window);
        if (reset <= now) return Unavailable("En attente d'un relevé confirmant la nouvelle période de quota.", window);
        var segment = new List<UsageSample> { latest };
        for (var i = history.Length - 2; i >= 0; i--)
        {
            var previous = history[i];
            var next = segment[^1];
            if (previous.AccountId != latest.AccountId || latest.Timestamp - previous.Timestamp > TimeSpan.FromHours(1) ||
                next.Timestamp - previous.Timestamp > MaximumObservationGap || Reset(previous, window) != reset ||
                Remaining(previous, window) is not { } priorRemaining || Remaining(next, window) > priorRemaining + 0.5) break;
            segment.Add(previous);
        }
        segment.Reverse();
        if (segment.Count < 3 || latest.Timestamp - segment[0].Timestamp < TimeSpan.FromMinutes(15))
            return Unavailable("Il faut au moins 15 minutes de suivi continu dans la même période de quota.", window);
        var first = segment[0];
        var elapsedHours = (latest.Timestamp - first.Timestamp).TotalHours;
        var consumed = Remaining(first, window)!.Value - remaining.Value;
        if (consumed <= 0.1 || elapsedHours <= 0) return Unavailable("Aucune consommation régulière mesurable sur cette période.", window);

        // Fit the observed trend, and decline to extrapolate a substantially irregular workload.
        var xs = segment.Select(p => (p.Timestamp - first.Timestamp).TotalHours).ToArray();
        var ys = segment.Select(p => Remaining(p, window)!.Value).ToArray();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var varianceX = xs.Sum(x => Math.Pow(x - meanX, 2));
        var covariance = xs.Select((x, i) => (x - meanX) * (ys[i] - meanY)).Sum();
        var slope = covariance / varianceX;
        var varianceY = ys.Sum(y => Math.Pow(y - meanY, 2));
        var rSquared = varianceY > 0 ? covariance * covariance / (varianceX * varianceY) : 0;
        if (!double.IsFinite(slope) || slope >= 0 || rSquared < 0.5)
            return Unavailable("Le rythme de consommation est trop irrégulier pour une estimation fiable.", window);
        var hours = remaining.Value / -slope;
        if (!double.IsFinite(hours) || hours > (reset.Value - latest.Timestamp).TotalHours)
            return Unavailable($"Au rythme observé, le reset du quota {label} devrait précéder son épuisement.", window);
        var exhaustion = latest.Timestamp + TimeSpan.FromHours(hours);
        var timeLeft = exhaustion > now ? exhaustion - now : TimeSpan.Zero;
        return new(timeLeft, exhaustion, $"Estimation du quota {label}, si le rythme observé reste comparable.", window);
    }

    public static bool IsValid(UsageSample sample) => sample.AccountId != Guid.Empty &&
        ValidPercent(sample.WeeklyRemaining) && ValidPercent(sample.ShortRemaining);
    private static bool ValidPercent(double? value) => value is null || (double.IsFinite(value.Value) && value >= 0 && value <= 100);
    private static double? Remaining(UsageSample sample, UsageWindowKind window) => window == UsageWindowKind.Weekly ? sample.WeeklyRemaining : sample.ShortRemaining;
    private static DateTimeOffset? Reset(UsageSample sample, UsageWindowKind window) => window == UsageWindowKind.Weekly ? sample.WeeklyResetsAt : sample.ShortResetsAt;
    private static UsageForecast Unavailable(string explanation, UsageWindowKind? window = null) => new(null, null, explanation, window);
}

public sealed record QuotaWindowAlertState(double Remaining, DateTimeOffset? ResetsAt, DateTimeOffset ObservedAt,
    int NotifiedThresholdMask = 0, DateTimeOffset? LastObservedReset = null);
public sealed record QuotaAlertState(QuotaWindowAlertState? Weekly = null, QuotaWindowAlertState? Short = null);
public sealed record QuotaAlertEvaluation(QuotaAlertState State, IReadOnlyList<QuotaNotification> Notifications);

public static class QuotaAlertEvaluator
{
    private static readonly int[] Thresholds = [20, 10, 5];

    public static QuotaAlertEvaluation Observe(AccountProfile profile, AccountSnapshot snapshot, QuotaAlertState? previous,
        bool suppressNotifications = false)
    {
        previous ??= new();
        var notifications = new List<QuotaNotification>();
        var weekly = ObserveWindow(profile, snapshot.Weekly, snapshot.FetchedAt, UsageWindowKind.Weekly,
            previous.Weekly, suppressNotifications, notifications);
        var shortWindow = ObserveWindow(profile, snapshot.Short, snapshot.FetchedAt, UsageWindowKind.Short,
            previous.Short, suppressNotifications, notifications);
        return new(new(weekly, shortWindow), notifications.AsReadOnly());
    }

    private static QuotaWindowAlertState? ObserveWindow(AccountProfile profile, QuotaWindow? window, DateTimeOffset observedAt,
        UsageWindowKind kind, QuotaWindowAlertState? previous, bool suppress, List<QuotaNotification> notifications)
    {
        if (window is null || !double.IsFinite(window.RemainingPercent)) return previous;
        var remaining = window.RemainingPercent;
        if (previous is null) return new(remaining, window.ResetsAt, observedAt, LastObservedReset: window.ResetsAt);
        if (observedAt <= previous.ObservedAt) return previous;
        var advances = previous.ResetsAt is { } oldReset && window.ResetsAt is { } newReset && newReset > oldReset;
        var newPeriod = advances && (previous.LastObservedReset is null || window.ResetsAt > previous.LastObservedReset);
        var mask = newPeriod ? 0 : previous.NotifiedThresholdMask;
        var lastReset = previous.LastObservedReset;
        if (window.ResetsAt is { } currentReset && (lastReset is null || currentReset > lastReset)) lastReset = currentReset;
        var continuous = !suppress && observedAt - previous.ObservedAt <= UsageAnalytics.MaximumObservationGap;
        if (continuous && newPeriod && remaining > previous.Remaining)
            notifications.Add(new(profile.Id, profile.Email, NotificationKind.Reset, null, observedAt, kind));
        if (continuous && !advances)
        {
            for (var i = 0; i < Thresholds.Length; i++)
            {
                var threshold = Thresholds[i];
                if (previous.Remaining > threshold && remaining <= threshold && (mask & (1 << i)) == 0)
                {
                    mask |= 1 << i;
                    notifications.Add(new(profile.Id, profile.Email, NotificationKind.Threshold, threshold, observedAt, kind));
                }
            }
        }
        return new(remaining, window.ResetsAt, observedAt, mask, lastReset);
    }
}
