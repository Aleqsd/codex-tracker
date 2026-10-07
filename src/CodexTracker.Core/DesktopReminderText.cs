namespace CodexTracker.Core;

public sealed record DesktopReminderText(string Title, string Body)
{
    public static DesktopReminderText For(IReadOnlyList<ReminderOccurrence> rows)
    {
        if (rows.Count == 0) throw new ArgumentException("At least one reminder is required.", nameof(rows));
        var first = rows.OrderByDescending(ReminderPlanner.IsGlobalReset).ThenByDescending(ReminderPlanner.IsExpectedReset).First();
        if (!ReminderPlanner.IsGlobalReset(first)) return new(rows.Count == 1 ? ReminderPlanner.Title(first) : $"{rows.Count} événements de quota",
            ReminderPlanner.Body(first) + (rows.Count > 1 ? $"\n{rows.Count - 1} autre(s) événement(s) dans l’onglet Resets." : ""));
        var related = rows.Where(r => ReminderPlanner.IsGlobalReset(r) && r.SourceUrl == first.SourceUrl && r.At == first.At).ToArray();
        var accounts = related.DistinctBy(r => r.AccountId).ToArray();
        var names = string.Join(" · ", accounts.Take(2).Select(a => a.AccountName.Length > 60 ? a.AccountName[..57] + "…" : a.AccountName));
        if (accounts.Length > 2) names += $" (+{accounts.Length - 2})";
        return new($"Reset général · {accounts.Length} compte{(accounts.Length == 1 ? "" : "s")} à vérifier",
            $"{names}\nQuota probablement rechargé, à confirmer dans Codex.\nAnnonce du {first.At.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}. Sources et relevés datés dans Resets." +
            (related.Length < rows.Count ? "\nD’autres rappels sont disponibles dans Resets." : ""));
    }
}
