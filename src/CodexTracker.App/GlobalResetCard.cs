namespace CodexTracker.App;

/// <summary>Public announcement and account-specific reasoning, separate from measured quota rows.</summary>
internal sealed class GlobalResetCard : Border
{
    private readonly GlobalResetAnnouncement _announcement;
    private readonly ResetKind[] _kinds;
    private readonly TextBlock _validity = Ui.Text("", 11, "MutedBrush");

    internal GlobalResetCard(GlobalResetAnnouncement announcement, AccountState[] accounts, ResetKind[] kinds,
        Func<AccountState, string> name, string? error, bool expanded, Action<bool> expansion)
    {
        _announcement = announcement; _kinds = kinds;
        Name = "GlobalResetNotice"; Padding = new Thickness(12, 10, 12, 10);
        BorderThickness = new Thickness(2, 0, 0, 0); Margin = new Thickness(0, 8, 0, 8);
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(BorderBrushProperty, "GoodBrush");
        var panel = new StackPanel(); Child = panel;
        var title = Ui.Text("Reset général · annoncé comme terminé", 13); title.FontWeight = FontWeights.SemiBold; panel.Children.Add(title);
        panel.Children.Add(Ui.Text($"{Display.Exact(announcement.ReportedAt)} · {Display.Zone(announcement.ReportedAt)}", 11, "MutedBrush"));
        var scope = announcement.Plans.Contains("free") ? "Tous les abonnements" : "Abonnements payants concernés";
        panel.Children.Add(Ui.Text(scope + " · " + string.Join(" + ", kinds.Select(k => k == ResetKind.Weekly ? "semaine" : "5 h")), 11, "MutedBrush"));
        var estimated = accounts.Count(a => kinds.Any(k => announcement.Applies(a, k, PreviewClock.UtcNow)));
        var summary = Ui.Text(estimated == 0 ? "Les quotas mesurés restent prioritaires · aucune estimation pour cette sélection."
            : $"{estimated} compte{(estimated == 1 ? "" : "s")} probablement rechargé{(estimated == 1 ? "" : "s")} · ≈100 % à confirmer", 12, estimated == 0 ? "MutedBrush" : "GoodBrush");
        summary.Margin = new Thickness(0, 6, 0, 2); panel.Children.Add(summary); panel.Children.Add(_validity);

        var details = new StackPanel();
        foreach (var account in accounts)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 6) };
            var identity = Ui.Text(name(account), 12); identity.FontWeight = FontWeights.SemiBold;
            identity.TextWrapping = TextWrapping.NoWrap; identity.TextTrimming = TextTrimming.CharacterEllipsis; identity.ToolTip = name(account);
            row.Children.Add(identity);
            foreach (var kind in kinds)
            {
                var status = announcement.StatusFor(account, kind, PreviewClock.UtcNow);
                row.Children.Add(Ui.Text((kind == ResetKind.Weekly ? "Semaine" : "5 h") + " · " + StatusLabel(status), 11,
                    status == GlobalResetAccountStatus.Estimated ? "GoodBrush" : "MutedBrush"));
            }
            row.ToolTip = $"Dernier relevé : {Display.Exact(account.Snapshot?.FetchedAt)}\n{Display.Zone(account.Snapshot?.FetchedAt)}\nSeuls les relevés antérieurs à l’annonce initiale du {Display.Exact(announcement.AnnouncedAt)} permettent une estimation.";
            details.Children.Add(row);
        }
        var expander = new Expander { Header = $"Détail des comptes ({accounts.Length})", Content = details, IsExpanded = expanded, Margin = new Thickness(0, 6, 0, 2) };
        expander.Expanded += (_, _) => expansion(true); expander.Collapsed += (_, _) => expansion(false); panel.Children.Add(expander);
        panel.Children.Add(Ui.Text("Les dates ci-dessous restent celles des anciens relevés ; aucune nouvelle échéance n’est inventée.", 11, "MutedBrush"));
        var links = new WrapPanel();
        foreach (var (label, url) in new[] { ("Voir la confirmation ↗", announcement.SourceUrl), ("Voir la portée ↗", announcement.AnnouncementUrl) }.DistinctBy(x => x.Item2))
        {
            var button = new Button { Content = label, ToolTip = url, Style = (Style)FindResource("QuietButton"), Padding = new Thickness(0, 6, 14, 0), FontSize = 11 };
            button.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { button.ToolTip = "Impossible d’ouvrir le navigateur. " + url; }
            };
            links.Children.Add(button);
        }
        panel.Children.Add(links);
        if (error is not null) panel.Children.Add(Ui.Text(error, 11, "WarningBrush"));
        ToolTip = $"Sources vérifiées le {Display.Exact(announcement.VerifiedAt)}. Le compte actif conserve toujours son quota mesuré.";
        Tick();
    }

    internal void Tick()
    {
        var expiry = _kinds.Max(_announcement.ExpiresAt);
        _validity.Text = expiry <= PreviewClock.UtcNow ? "Estimation expirée · ouvrir les comptes dans Codex pour vérifier."
            : "Estimation valable encore " + Display.Countdown(expiry).Replace("Dans ", "");
        _validity.ToolTip = $"Fin de validité : {Display.Exact(expiry)}\n{Display.Zone(expiry)}\nL’estimation est retirée plus tôt dès qu’un nouveau relevé est reçu.";
    }

    internal static string StatusLabel(GlobalResetAccountStatus status) => status switch
    {
        GlobalResetAccountStatus.Estimated => "≈100 % estimé · à confirmer dans Codex",
        GlobalResetAccountStatus.Active => "Compte actif · quota mesuré conservé",
        GlobalResetAccountStatus.NewerObservation => "Relevé plus récent · quota mesuré conservé",
        GlobalResetAccountStatus.NotCovered => "Hors de la portée annoncée",
        GlobalResetAccountStatus.OldObservation => "Relevé trop ancien pour estimer",
        GlobalResetAccountStatus.SubscriptionEnded => "Période d’abonnement à reconfirmer",
        GlobalResetAccountStatus.Expired => "Estimation expirée",
        _ => "Données insuffisantes pour estimer"
    };
}
