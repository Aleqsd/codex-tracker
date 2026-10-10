namespace CodexTracker.App;

/// <summary>Public announcement and account-specific reasoning, separate from measured quota rows.</summary>
internal sealed class GlobalResetCard : Border
{
    private readonly GlobalResetAnnouncement _announcement;
    private readonly ResetKind[] _kinds;
    private readonly TextBlock _validity = Ui.Text("", 11, "MutedBrush");

    internal GlobalResetCard(GlobalResetAnnouncement announcement, AccountState[] accounts, ResetKind[] kinds,
        Func<AccountState, string> name, string? error, bool expanded, Action<bool> expansion, ManualCodexReset? manualReset = null)
    {
        _announcement = announcement; _kinds = kinds;
        Name = "GlobalResetNotice"; Padding = new Thickness(16, 13, 16, 12);
        CornerRadius = new CornerRadius(12); Margin = new Thickness(0, 8, 0, 8);
        SetResourceReference(BackgroundProperty, "GoodSoftBrush");
        var panel = new StackPanel(); Child = panel;
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(Ui.Icon("DropdownRefreshIcon", 16, "GoodBrush", 2));
        var title = Ui.Text(Loc.T("Reset général · annoncé comme terminé"), 13); title.FontWeight = FontWeights.Medium; title.Margin = new Thickness(9, 0, 0, 0); heading.Children.Add(title);
        panel.Children.Add(heading);
        var reported = Ui.Text($"{Display.Exact(announcement.ReportedAt)} · {Display.Zone(announcement.ReportedAt)}", 11, "MutedBrush"); reported.Margin = new Thickness(0, 6, 0, 0); panel.Children.Add(reported);
        var scope = announcement.Plans.Contains("free") ? Loc.T("Tous les abonnements") : Loc.T("Abonnements payants concernés");
        panel.Children.Add(Ui.Text(scope + " · " + string.Join(" + ", kinds.Select(k => k == ResetKind.Weekly ? Loc.T("semaine") : "5 h")), 11, "MutedBrush"));
        bool Declared(AccountState account, ResetKind kind) => manualReset?.Applies(account, kind, PreviewClock.UtcNow) == true;
        var declared = accounts.Any(a => kinds.Any(k => Declared(a, k)));
        var estimated = accounts.Count(a => kinds.Any(k => !Declared(a, k) && announcement.Applies(a, k, PreviewClock.UtcNow)));
        var summary = Ui.Text(estimated == 0 ? declared ? Loc.T("Le reset déclaré manuellement est prioritaire pour les comptes concernés.") : Loc.T("Les quotas mesurés restent prioritaires · aucune estimation pour cette sélection.")
            : estimated == 1 ? Loc.T("1 compte probablement rechargé · ≈100 % à confirmer") : Loc.F("{0} comptes probablement rechargés · ≈100 % à confirmer", estimated), 12, estimated == 0 ? "MutedBrush" : "GoodBrush");
        summary.Margin = new Thickness(0, 6, 0, 2); panel.Children.Add(summary); panel.Children.Add(_validity);

        var details = new StackPanel();
        foreach (var account in accounts)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 6) };
            var identity = Ui.Text(name(account), 12); identity.FontWeight = FontWeights.Medium;
            identity.TextWrapping = TextWrapping.NoWrap; identity.TextTrimming = TextTrimming.CharacterEllipsis; identity.ToolTip = name(account);
            row.Children.Add(identity);
            foreach (var kind in kinds)
            {
                var window = kind == ResetKind.Weekly ? Loc.T("Semaine") : "5 h";
                if (Declared(account, kind))
                {
                    row.Children.Add(Ui.Text(window + " · " + Loc.T("100 % déclaré · reset manuel prioritaire"), 11, "GoodBrush"));
                    continue;
                }
                var status = announcement.StatusFor(account, kind, PreviewClock.UtcNow);
                row.Children.Add(Ui.Text(window + " · " + StatusLabel(status), 11,
                    status == GlobalResetAccountStatus.Estimated ? "GoodBrush" : "MutedBrush"));
            }
            row.ToolTip = Loc.F("Dernier relevé : {0}", Display.Exact(account.Snapshot?.FetchedAt)) + "\n" + Display.Zone(account.Snapshot?.FetchedAt) + "\n"
                + Loc.F("Seuls les relevés antérieurs à l’annonce initiale du {0} permettent une estimation.", Display.Exact(announcement.AnnouncedAt));
            details.Children.Add(row);
        }
        var expander = new Expander { Header = Loc.F("Détail des comptes ({0})", accounts.Length), Content = details, IsExpanded = expanded, Margin = new Thickness(0, 6, 0, 2) };
        expander.Expanded += (_, _) => expansion(true); expander.Collapsed += (_, _) => expansion(false); panel.Children.Add(expander);
        panel.Children.Add(Ui.Text(Loc.T("Les dates ci-dessous restent celles des anciens relevés ; aucune nouvelle échéance n’est inventée."), 11, "MutedBrush"));
        var links = new WrapPanel();
        foreach (var (label, url) in new[] { (Loc.T("Voir la confirmation ↗"), announcement.SourceUrl), (Loc.T("Voir la portée ↗"), announcement.AnnouncementUrl) }.DistinctBy(x => x.Item2))
        {
            var button = new Button { Content = label, ToolTip = url, Style = (Style)FindResource("LinkButton"), Padding = new Thickness(0, 6, 16, 2), FontSize = 12 };
            button.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { button.ToolTip = Loc.F("Impossible d’ouvrir le navigateur. {0}", url); }
            };
            links.Children.Add(button);
        }
        panel.Children.Add(links);
        if (error is not null) panel.Children.Add(Ui.Text(error, 11, "WarningBrush"));
        ToolTip = Loc.F("Sources vérifiées le {0}. Les nouveaux relevés et les déclarations manuelles restent prioritaires.", Display.Exact(announcement.VerifiedAt));
        Tick();
    }

    internal void Tick()
    {
        var expiry = _kinds.Max(_announcement.ExpiresAt);
        var left = expiry - PreviewClock.UtcNow;
        _validity.Text = left <= TimeSpan.Zero ? Loc.T("Estimation expirée · ouvrir les comptes dans Codex pour vérifier.")
            : Loc.F("Estimation valable encore {0}", Remaining(left));
        _validity.ToolTip = Loc.F("Fin de validité : {0}", Display.Exact(expiry)) + "\n" + Display.Zone(expiry) + "\n"
            + Loc.T("L’estimation est retirée plus tôt dès qu’un nouveau relevé est reçu.");
    }

    /// <summary>Positive remaining time with the same units as <see cref="Display.Countdown"/>, without its "Dans" prefix.</summary>
    internal static string Remaining(TimeSpan left) =>
        left.TotalDays >= 1 ? Loc.F("{0} j {1:00} h", (int)left.TotalDays, left.Hours)
        : left.TotalHours >= 1 ? Loc.F("{0} h {1:00} min", (int)left.TotalHours, left.Minutes)
        : Loc.F("{0} min {1:00} s", left.Minutes, left.Seconds);

    internal static string StatusLabel(GlobalResetAccountStatus status) => status switch
    {
        GlobalResetAccountStatus.Estimated => Loc.T("≈100 % estimé · à confirmer dans Codex"),
        GlobalResetAccountStatus.Active => Loc.T("Compte actif · quota mesuré conservé"),
        GlobalResetAccountStatus.NewerObservation => Loc.T("Relevé plus récent · quota mesuré conservé"),
        GlobalResetAccountStatus.NotCovered => Loc.T("Hors de la portée annoncée"),
        GlobalResetAccountStatus.OldObservation => Loc.T("Relevé trop ancien pour estimer"),
        GlobalResetAccountStatus.SubscriptionEnded => Loc.T("Période d’abonnement à reconfirmer"),
        GlobalResetAccountStatus.Expired => Loc.T("Estimation expirée"),
        _ => Loc.T("Données insuffisantes pour estimer")
    };
}
