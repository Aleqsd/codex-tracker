namespace CodexTracker.Core;

public sealed record DesktopReminderText(string Title, string Body)
{
    public static DesktopReminderText For(IReadOnlyList<ReminderOccurrence> rows)
    {
        if (rows.Count == 0) throw new ArgumentException("At least one reminder is required.", nameof(rows));
        var first = rows.OrderByDescending(ReminderPlanner.IsGlobalReset).ThenByDescending(ReminderPlanner.IsExpectedReset).First();
        if (!ReminderPlanner.IsGlobalReset(first)) return new(rows.Count == 1 ? ReminderPlanner.Title(first) : Loc.F("{0} événements de quota", rows.Count),
            ReminderPlanner.Body(first) + (rows.Count > 1 ? "\n" + (rows.Count == 2 ? Loc.T("1 autre(s) événement(s) dans l’onglet Resets.")
                : Loc.F("{0} autre(s) événement(s) dans l’onglet Resets.", rows.Count - 1)) : ""));
        var related = rows.Where(r => ReminderPlanner.IsGlobalReset(r) && r.SourceUrl == first.SourceUrl && r.At == first.At).ToArray();
        var accounts = related.DistinctBy(r => r.AccountId).ToArray();
        var names = string.Join(" · ", accounts.Take(2).Select(a => a.AccountName.Length > 60 ? a.AccountName[..57] + "…" : a.AccountName));
        if (accounts.Length > 2) names += $" (+{accounts.Length - 2})";
        return new(accounts.Length == 1 ? Loc.T("Reset général · 1 compte à vérifier") : Loc.F("Reset général · {0} comptes à vérifier", accounts.Length),
            Loc.F("{0}\nQuota probablement rechargé, à confirmer dans Codex.\nAnnonce du {1:dd/MM/yyyy HH:mm:ss zzz}. Sources et relevés datés dans Resets.", names, first.At.ToLocalTime()) +
            (related.Length < rows.Count ? "\n" + Loc.T("D’autres rappels sont disponibles dans Resets.") : ""));
    }
}
