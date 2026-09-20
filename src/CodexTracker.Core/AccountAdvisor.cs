using System.Globalization;

namespace CodexTracker.Core;

public enum AccountAdviceKind { CurrentComfortable, VerifyInCodex, InsufficientData }
public enum AccountAdviceConfidence { RecentObservation, HistoricalObservation, InsufficientData }

public sealed record AccountAdvice(Guid? AccountId, AccountAdviceKind Kind, AccountAdviceConfidence Confidence,
    string Label, string Reason, DateTimeOffset? ObservedAt, DateTimeOffset? NextResetAt);

/// <summary>
/// A qualitative reading of dated quota observations. This does not authenticate, switch accounts,
/// infer a refill from a deadline, or compare the absolute capacity of different subscriptions.
/// </summary>
public static class AccountAdvisor
{
    public const double LowQuotaThreshold = 20;
    public static readonly TimeSpan MaximumActiveAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumInactiveAge = TimeSpan.FromHours(2);

    public static AccountAdvice Evaluate(TrackerState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var activeAccounts = state.Accounts.Where(a => a.IsActiveInCodex).ToArray();
        if (activeAccounts.Length != 1 || state.Accounts.GroupBy(a => a.Profile.Id).Any(g => g.Count() > 1) ||
            state.Accounts.GroupBy(a => a.Profile.Email, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return Unavailable("Le compte actuellement ouvert dans Codex n’est pas identifié avec certitude.");

        var active = activeAccounts[0];
        if (!TryObserve(active, now, MaximumActiveAge, out var current, out var unavailableReason))
            return Unavailable(unavailableReason);

        if (Comfortable(current!))
            return new(active.Profile.Id, AccountAdviceKind.CurrentComfortable, AccountAdviceConfidence.RecentObservation,
                "Compte actuel confortable",
                $"Les quotas de 5 heures et de la semaine dépassent 20 % dans le relevé du {Stamp(current!.FetchedAt)}.",
                current.FetchedAt, NextReset(current));

        // Every eligible account meets the same qualitative condition. Recency, rather than raw
        // percentages or a subscription multiplier, chooses which historical observation to show.
        var candidate = state.Accounts.Where(a => !a.IsActiveInCodex)
            .Where(a => TryObserve(a, now, MaximumInactiveAge, out var snapshot, out _) && Comfortable(snapshot!))
            .OrderByDescending(a => a.Snapshot!.FetchedAt)
            .ThenBy(a => a.Profile.Id)
            .FirstOrDefault();

        if (candidate is null)
            return Unavailable("Un quota du compte actuel est à 20 % ou moins. Aucun autre relevé assez récent, complet et sans reset atteint ne permet de suggérer un compte à vérifier.",
                current!.FetchedAt, NextReset(current));

        var previous = candidate.Snapshot!;
        return new(candidate.Profile.Id, AccountAdviceKind.VerifyInCodex, AccountAdviceConfidence.HistoricalObservation,
            "À vérifier dans Codex",
            $"Un quota du compte actuel est à 20 % ou moins. Au dernier relevé du {Stamp(previous.FetchedAt)}, ce compte avait plus de 20 % sur les deux fenêtres. Ses quotas actuels restent à vérifier dans Codex ; les capacités des offres ne sont pas comparées.",
            previous.FetchedAt, NextReset(previous));
    }

    private static bool TryObserve(AccountState account, DateTimeOffset now, TimeSpan maximumAge,
        out AccountSnapshot? snapshot, out string reason)
    {
        snapshot = account.Snapshot;
        reason = "Un relevé complet et récent du compte actuel est nécessaire avant de proposer un autre compte.";
        if (account.Profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(account.Profile.Email) ||
            !account.IsConnected || account.Error is not null)
        {
            reason = "La session ou le dernier relevé du compte actuel doit être vérifié dans Codex.";
            return false;
        }
        if (account.IsRefreshing)
        {
            reason = "La lecture du compte actuel est en cours. Le conseil attend son résultat.";
            return false;
        }
        if (snapshot is null || !string.Equals(snapshot.Email, account.Profile.Email, StringComparison.OrdinalIgnoreCase))
            return false;
        if (snapshot.FetchedAt > now || now - snapshot.FetchedAt > maximumAge)
        {
            reason = "Le dernier relevé du compte actuel est trop ancien ou sa date est incohérente. Actualisez les quotas avant de choisir.";
            return false;
        }
        var weekly = snapshot.Weekly;
        var shortWindow = snapshot.Short;
        if (!KnownWindow(weekly) || !KnownWindow(shortWindow))
        {
            reason = "Les deux quotas, de 5 heures et de la semaine, et leurs dates de reset doivent être connus pour donner un conseil.";
            return false;
        }
        if (weekly!.ResetsAt <= now || shortWindow!.ResetsAt <= now)
        {
            reason = "Une date de reset est atteinte. Un nouveau relevé doit confirmer les quotas ; aucun rechargement n’est supposé.";
            return false;
        }
        return true;
    }

    private static bool KnownWindow(QuotaWindow? window) => window is not null &&
        double.IsFinite(window.UsedPercent) && window.UsedPercent is >= 0 and <= 100 && window.ResetsAt is not null;

    private static bool Comfortable(AccountSnapshot snapshot) =>
        snapshot.Weekly!.RemainingPercent > LowQuotaThreshold && snapshot.Short!.RemainingPercent > LowQuotaThreshold;

    private static DateTimeOffset NextReset(AccountSnapshot snapshot) =>
        snapshot.Weekly!.ResetsAt!.Value <= snapshot.Short!.ResetsAt!.Value
            ? snapshot.Weekly.ResetsAt.Value : snapshot.Short.ResetsAt.Value;

    private static string Stamp(DateTimeOffset timestamp) => timestamp.ToString("dd/MM/yyyy HH:mm:ss 'UTC'zzz", CultureInfo.InvariantCulture);

    private static AccountAdvice Unavailable(string reason, DateTimeOffset? observedAt = null, DateTimeOffset? nextResetAt = null) =>
        new(null, AccountAdviceKind.InsufficientData, AccountAdviceConfidence.InsufficientData,
            "Conseil indisponible", reason, observedAt, nextResetAt);
}
