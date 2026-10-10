using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CodexTracker.App;

internal static class Display
{
    public static Brush Green => ThemeManager.GetBrush("GoodBrush");
    public static Brush Orange => ThemeManager.GetBrush("WarningBrush");
    public static Brush Red => ThemeManager.GetBrush("DangerBrush");
    public static Brush Muted => ThemeManager.GetBrush("MutedBrush");
    public static Brush Brush(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    public static Brush QuotaBrush(double? value) => value is null ? Muted : value < 10 ? Red : value <= 20 ? Orange : ThemeManager.GetBrush("TextBrush");
    // Gauges carry the accent; text keeps the neutral colour until a threshold is reached.
    public static Brush QuotaBarBrush(double? value) => value is null ? ThemeManager.GetBrush("SubtleBrush") : value < 10 ? Red : value <= 20 ? Orange : ThemeManager.GetBrush("AccentBrush");
    public static string Percent(double? value) => value is null ? "—" : $"{Math.Floor(value.Value):0}%";
    /// <summary>Inferred Claude weekly reset: the bracket is shown instead of a single precise date.</summary>
    public static string EstimatedReset(QuotaWindow window) => (window.EstimatedResetFrom == window.ResetsAt
        ? Loc.F("Reset hebdomadaire estimé le {0}.\nProjeté depuis la dernière date fournie par Claude Code : Claude renouvelle la semaine au même moment chaque semaine.\nUne nouvelle date de Claude Code remplace cette estimation.", Exact(window.ResetsAt))
        : Loc.F("Reset hebdomadaire estimé entre le {0} et le {1}.\nDéduit de la dernière remise à zéro de la semaine observée : Claude la renouvelle au même moment chaque semaine.\nUne date fournie par Claude Code remplace cette estimation.",
        Exact(window.EstimatedResetFrom), Exact(window.ResetsAt))) + "\n" + Zone(window.ResetsAt);
    /// <summary>The Claude application gives no reset date: how to get one automatically.</summary>
    public static string ClaudeResetHelp => Loc.T("L’application Claude ne communique pas la date du reset.\nPour l’obtenir automatiquement :\n1. Installez Claude Code, puis connectez-vous avec ce compte Claude (/login).\n2. Réglages → Général → « Copier le réglage Claude Code », puis collez-le dans ~/.claude/settings.json.\n3. Envoyez un message dans une nouvelle session Claude Code.\nLa date relevée est ensuite reprojetée chaque semaine. Sans Claude Code, elle sera estimée après la prochaine remise à zéro observée.");
    public static string Exact(DateTimeOffset? date) => date?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", Loc.Culture) ?? Loc.T("Indisponible");
    public static string Zone(DateTimeOffset? date)
    {
        if (date is null) return Loc.T("Date non communiquée");
        string local = TimeZoneInfo.Local.Id;
        string zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(local, out var iana) ? iana : local;
        return $"{zone} · UTC{date.Value.ToLocalTime():zzz}";
    }
    /// <summary>"Dans 2 j 14 h": the countdown sentence. Use <see cref="Remaining"/> for the bare duration.</summary>
    public static string Countdown(DateTimeOffset? date)
    {
        if (date is null) return Loc.T("Indisponible");
        var left = date.Value - PreviewClock.UtcNow;
        return left <= TimeSpan.Zero ? Loc.T("Reset attendu") : Loc.F("Dans {0}", Span(left));
    }
    /// <summary>"2 j 14 h" without the leading "Dans", in either language; unknown or past dates keep their wording.</summary>
    public static string Remaining(DateTimeOffset? date)
    {
        if (date is null) return Loc.T("Indisponible");
        var left = date.Value - PreviewClock.UtcNow;
        return left <= TimeSpan.Zero ? Loc.T("Reset attendu") : Span(left);
    }
    private static string Span(TimeSpan left) =>
        left.TotalDays >= 1 ? Loc.F("{0} j {1:00} h", (int)left.TotalDays, left.Hours)
        : left.TotalHours >= 1 ? Loc.F("{0} h {1:00} min", (int)left.TotalHours, left.Minutes)
        : Loc.F("{0} min {1:00} s", left.Minutes, left.Seconds);
    public static string Duration(int? minutes) => minutes switch { null => Loc.T("Fenêtre inconnue"), 10080 => Loc.T("Semaine"), 300 => Loc.T("5 heures"), >= 1440 => Loc.F("{0:0.#} jours", minutes / 1440.0), >= 60 => Loc.F("{0:0.#} heures", minutes / 60.0), _ => Loc.F("{0} min", minutes) };
    public static string Age(DateTimeOffset timestamp)
    {
        var age = PreviewClock.UtcNow - timestamp;
        return age.TotalMinutes < 1 ? Loc.T("à l’instant") : age.TotalHours < 1 ? Loc.F("il y a {0} min", (int)age.TotalMinutes) : age.TotalDays < 1 ? Loc.F("il y a {0} h", (int)age.TotalHours) : Loc.F("il y a {0} j", (int)age.TotalDays);
    }
    public static string SafeText(string? text, bool privacy) => privacy ? Regex.Replace(text ?? "", @"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+", Loc.T("[compte masqué]"), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : text ?? "";
    public static string ReserveSummary(AccountSnapshot? snapshot) => snapshot?.AvailableResetCredits is int count
        ? count == 1 ? Loc.T("1 reset en réserve") : Loc.F("{0} resets en réserve", count) : Loc.T("Réserve indisponible");
    /// <summary>"Expire le …" or "Expiration passée …" for a reset credit.</summary>
    public static string Expiry(DateTimeOffset expires, DateTimeOffset now) =>
        expires <= now ? Loc.F("Expiration passée {0}", Exact(expires)) : Loc.F("Expire le {0}", Exact(expires));
    public static string CreditDetails(AccountSnapshot? snapshot, bool privacy) => snapshot?.ResetCredits is { Count: > 0 } credits
        ? string.Join("\n\n", credits.OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).Select((c, i) =>
            $"{SafeText(c.Title ?? $"Reset {i + 1}", privacy)}\n" +
            (c.GrantedAt is { } granted ? Loc.F("Reçu le {0} · {1}", Exact(granted), Zone(granted)) + "\n" : "") +
            (c.ExpiresAt is { } expires ? $"{Expiry(expires, PreviewClock.UtcNow)}\n{Zone(expires)}" : Loc.T("Expiration non communiquée"))))
        : Loc.T("Aucune expiration communiquée par Codex.");
    public static string ReserveHint(AccountSnapshot? snapshot, bool privacy) =>
        $"{ReserveSummary(snapshot)}\n\n{CreditDetails(snapshot, privacy)}" +
        (snapshot is null ? "" : "\n\n" + Loc.F("Dernier relevé : {0}\n{1}\nDisponibilité au moment de ce relevé.", Exact(snapshot.FetchedAt), Zone(snapshot.FetchedAt)));
}

internal sealed class AccountViewModel : INotifyPropertyChanged
{
    private AccountState _account;
    private TrackerState _state;
    private readonly PreferencesStore _preferences;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AccountViewModel(AccountState account, TrackerState state, PreferencesStore preferences, Func<Guid, UsageForecast>? forecast = null)
    { _account = account; _state = state; _preferences = preferences; _forecast = forecast; }
    private readonly Func<Guid, UsageForecast>? _forecast;
    private UsageForecast? Forecast => IsActive ? _forecast?.Invoke(Id) : null;
    public bool HasForecast => Forecast is { EstimatedExhaustionAt: not null } or { LastsUntilReset: true };
    public string ForecastLabel => Forecast?.EstimatedExhaustionAt is not null ? Loc.T("Épuisé dans") : Loc.T("À ce rythme");
    public string ForecastText => Forecast?.EstimatedExhaustionAt is { } at ? "≈ " + Display.Remaining(at) : Loc.T("jusqu’au reset");
    public Brush ForecastBrush => Forecast?.EstimatedExhaustionAt is not null ? Display.Orange : Display.Green;
    public string ForecastHint => Forecast is not { } forecast ? "" : forecast.EstimatedExhaustionAt is { } at
        ? forecast.Explanation + "\n" + Loc.F("Épuisement estimé : {0}\n{1}", Display.Exact(at), Display.Zone(at)) : forecast.Explanation;
    public void Update(AccountState account, TrackerState state) { _account = account; _state = state; Tick(); }
    public void Tick() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private TrackerState PresentationState => _state with { ManualCodexReset = _preferences.Current.ManualCodexReset };
    public bool HasManualWeeklyReset => PresentationState.ManualCodexReset?.Applies(_account, ResetKind.Weekly, PreviewClock.UtcNow) == true;
    public bool HasManualShortReset => PresentationState.ManualCodexReset?.Applies(_account, ResetKind.Short, PreviewClock.UtcNow) == true;
    public bool HasManualReset => HasManualWeeklyReset || HasManualShortReset;
    public string WeeklyDisplayLabel => HasManualWeeklyReset ? Loc.T("restant · déclaré") : Loc.T("restant");
    public string ManualResetHint => !HasManualReset ? "" : Loc.F("Reset Codex déclaré pour le {0} · {1}.\n100 % déclaré, sous réserve d’une utilisation depuis le reset.\nDernier quota mesuré : {2} le {3}.\nLes prochains relevés mesurés sont prioritaires.",
        Display.Exact(PresentationState.ManualCodexReset!.At), Display.Zone(PresentationState.ManualCodexReset.At), Display.Percent(_account.Snapshot?.Weekly?.RemainingPercent), Display.Exact(_account.Snapshot?.FetchedAt));
    public Guid Id => _account.Profile.Id;
    public string Email => PrivacyText.Account(_account.Profile, _state, _preferences.Current);
    public ImageSource? Avatar => _preferences.Current.PrivacyMode ? null : AvatarStore.Load(_preferences.DataDirectory, _preferences.Current.Appearances.GetValueOrDefault(Id)?.AvatarFile);
    public bool HasAvatar => Avatar is not null;
    public string IdentityHint => _preferences.Current.PrivacyMode ? Email : _account.Profile.Email;
    public string Initials => AccountAvatar.Initials(Email);
    public Brush AvatarBackground => HasAvatar ? ThemeManager.GetBrush("AvatarBrush") : AccountAvatar.Background(Id);
    public string ProviderName => _account.Profile.ProviderName;
    public bool IsClaude => _account.Profile.Provider == AccountProvider.ClaudeCode;
    public bool ShowProviderHeader { get; private set; }
    public bool IsLastInSection { get; private set; }
    public string ProviderCount { get; private set; } = "";
    internal void SetProviderHeader(bool show, int count, bool last)
    {
        ShowProviderHeader = show; IsLastInSection = last; ProviderCount = count == 1 ? Loc.T("1 compte") : Loc.F("{0} comptes", count); Tick();
    }
    public bool HasReserves => _account.Profile.Provider == AccountProvider.Codex;
    public bool IsActive => _account.IsActive;
    public bool IsSelected => IsActive;
    public bool IsIdle => !_state.IsBusy;
    public bool CanRemove => IsIdle && !IsActive;
    public string Plan => (_account.Snapshot?.PlanType ?? _account.Profile.ProviderPlanType)?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", "team" => Loc.T("Équipe"), "enterprise" => Loc.T("Entreprise"), string other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other), _ => _account.IsConnected ? Loc.T("Offre inconnue") : Loc.T("À détecter") };
    public string PlanBadge => (_account.Snapshot?.PlanMultiplier ?? _account.Profile.ProviderPlanMultiplier) is int multiplier ? $"{Plan} {multiplier}×" : Plan;
    public string CompactStatus => HasError ? Loc.T("à vérifier") : IsActive ? Loc.T("actif") : _account.Snapshot is not null ? Display.Age(_account.Snapshot.FetchedAt) : Loc.T("à détecter");
    public string Organization => _preferences.Current.PrivacyMode ? "" : _account.Profile.OrganizationName is { } name ? " · " + name : "";
    public string RowSubtitle => $"{PlanBadge}{Organization} · {CompactStatus}";
    public string SummaryLabel => IsActive ? Loc.F("Compte actif dans {0}{1}", ProviderName, Organization) : Loc.F("{0}{1} · dernier relevé disponible", ProviderName, Organization);
    private ExpectedReset? WeeklyEstimate => ExpectedReset.For(_account, ResetKind.Weekly, PreviewClock.UtcNow, _state.GlobalResetFeed, PresentationState.ManualCodexReset);
    private ExpectedReset? ShortEstimate => ExpectedReset.For(_account, ResetKind.Short, PreviewClock.UtcNow, _state.GlobalResetFeed, PresentationState.ManualCodexReset);
    public GlobalResetAnnouncement? GlobalAnnouncement => WeeklyEstimate?.Announcement ?? ShortEstimate?.Announcement;
    public bool HasWeeklyEstimate => WeeklyEstimate is not null;
    public bool HasResetEstimate => HasWeeklyEstimate || ShortEstimate is not null;
    public bool HasResetNotice => HasResetEstimate || HasManualReset;
    public string WeeklyDisplayNumber => HasManualWeeklyReset ? "100%" : HasWeeklyEstimate ? "≈100%" : WeeklyNumber;
    public Brush WeeklyDisplayBrush => HasManualWeeklyReset || HasWeeklyEstimate ? Display.Green : WeeklyBrush;
    public string EstimateLabel => HasManualReset ? Loc.T("Reset Codex déclaré") : HasWeeklyEstimate && ShortEstimate is not null ? Loc.T("Semaine + 5 h probablement à 100 %")
        : HasWeeklyEstimate ? Loc.T("Semaine probablement à 100 %") : ShortEstimate is not null ? Loc.T("5 h probablement à 100 %") : "";
    public string EstimateHint => HasManualReset ? ManualResetHint : string.Join("\n\n", new[] { WeeklyEstimate, ShortEstimate }.OfType<ExpectedReset>().Select(r =>
        (r.Announcement is { } a
            ? Loc.F("Reset général annoncé comme terminé le {0} · {1}.\nSource : {2}\nPortée : {3}\nEstimation valable jusqu’au {4}.", Display.Exact(a.ReportedAt), Display.Zone(a.ReportedAt), a.SourceUrl, a.AnnouncementUrl, Display.Exact(a.ReportedAt.AddHours(r.Kind == ResetKind.Short ? 5 : 24)))
            : Loc.F("{0} prévu le {1} · {2}.", ReminderPlanner.Label(r.Kind), Display.Exact(r.At), Display.Zone(r.At))) + "\n" +
        Loc.F("Dernier quota mesuré : {0} le {1}.\nProbablement revenu à 100 % si le compte n’a pas été utilisé ailleurs. Ouvrez ce compte dans {2} pour confirmer.", Display.Percent(r.LastRemainingPercent), Display.Exact(r.ObservedAt), ProviderName)));
    public string WeeklyHint => HasManualWeeklyReset ? ManualResetHint : HasWeeklyEstimate ? EstimateHint : Loc.F("Dernier quota mesuré : {0}\nRelevé : {1}", WeeklyNumber, Display.Exact(_account.Snapshot?.FetchedAt));
    public string WeeklyNumber => Display.Percent(QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Weekly, PreviewClock.UtcNow));
    public double WeeklyPercent => QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Weekly, PreviewClock.UtcNow) ?? 0;
    public Brush WeeklyBrush => HasManualWeeklyReset ? Display.Green : Display.QuotaBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public Brush WeeklyBarBrush => HasManualWeeklyReset ? Display.Green : Display.QuotaBarBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public double WeeklyDisplayPercent => HasManualWeeklyReset || HasWeeklyEstimate ? 100 : WeeklyPercent;
    public Brush WeeklyDisplayBarBrush => HasManualWeeklyReset || HasWeeklyEstimate ? Display.Green : WeeklyBarBrush;
    public string WeeklyRingCaption => HasManualWeeklyReset ? Loc.T("déclaré") : Loc.T("semaine");
    public string ShortWindowRemaining => Display.Percent(QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Short, PreviewClock.UtcNow));
    public double ShortPercent => QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Short, PreviewClock.UtcNow) ?? 0;
    public Brush ShortBarBrush => HasManualShortReset ? Display.Green : Display.QuotaBarBrush(_account.Snapshot?.Short?.RemainingPercent);
    public string ShortSummary => HasManualShortReset ? Loc.F("5 h : {0} · déclaré", ShortWindowRemaining) : Loc.F("5 h : {0}", ShortWindowRemaining);
    public string ResetExact => Display.Exact(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetZone => Display.Zone(_account.Snapshot?.Weekly?.ResetsAt);
    private bool WeeklyResetInferred => _account.Snapshot?.Weekly?.IsResetEstimated == true && !HasManualWeeklyReset && !HasWeeklyEstimate;
    private string Approximate => WeeklyResetInferred ? "≈ " : "";
    public string ResetCountdown => HasManualWeeklyReset ? Loc.T("À reconfirmer") : Approximate + Display.Countdown(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCompact => WeeklyEstimate?.Announcement is not null ? Loc.T("À reconfirmer") : HasWeeklyEstimate ? Loc.T("Reset passé")
        : HasManualWeeklyReset ? Loc.T("À reconfirmer") : Approximate + Display.Remaining(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetHint => HasManualWeeklyReset ? ManualResetHint : HasWeeklyEstimate ? EstimateHint
        : WeeklyResetInferred ? Display.EstimatedReset(_account.Snapshot!.Weekly!) : NeedsResetHelp ? Display.ClaudeResetHelp : $"{ResetExact}\n{ResetZone}";
    /// <summary>A measured Claude week without any date: the tooltip explains how to obtain one.</summary>
    public bool NeedsResetHelp => IsClaude && _account.Snapshot?.Weekly is { ResetsAt: null } && !HasWeeklyEstimate;
    // Both languages start the countdown with a capital ("Dans …", "In …"); the sentence continues it in lower case.
    public string SummaryReset => Loc.F("Reset hebdomadaire {0}", ResetCountdown.ToLowerInvariant());
    public string ReserveCount => _account.Snapshot?.AvailableResetCredits?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ReserveSummary => HasReserves ? Display.ReserveSummary(_account.Snapshot) : Loc.T("Pas de resets en réserve");
    public string ReserveBadge => $"↺ {ReserveCount}";
    public Brush ReserveBrush => ThemeManager.GetBrush(_account.Snapshot?.AvailableResetCredits > 0 ? "TextBrush" : "MutedBrush");
    public string ReserveHint => HasReserves ? Display.ReserveHint(_account.Snapshot, _preferences.Current.PrivacyMode) : Loc.T("Claude Code ne communique pas de resets en réserve.");
    public string SubscriptionSummary
    {
        get
        {
            if (_account.Snapshot?.SubscriptionStartedAt is null && _account.Snapshot?.SubscriptionEndsAt is null) return Loc.T("Période indisponible");
            string start = _account.Snapshot?.SubscriptionStartedAt?.ToLocalTime().ToString("dd/MM/yyyy", Loc.Culture) ?? Loc.T("début inconnu");
            string end = _account.Snapshot?.SubscriptionEndsAt?.ToLocalTime().ToString("dd/MM/yyyy", Loc.Culture) ?? Loc.T("fin inconnue");
            return $"{start} → {end}";
        }
    }
    public string SubscriptionDetails => Loc.F("Période d’abonnement active\nDébut : {0}\n{1}\nFin : {2}\n{3}",
        Display.Exact(_account.Snapshot?.SubscriptionStartedAt), Display.Zone(_account.Snapshot?.SubscriptionStartedAt), Display.Exact(_account.Snapshot?.SubscriptionEndsAt), Display.Zone(_account.Snapshot?.SubscriptionEndsAt));
    public string CreditDetails => Display.CreditDetails(_account.Snapshot, _preferences.Current.PrivacyMode);
    public bool HasError => !string.IsNullOrWhiteSpace(_account.Error);
    public string Error => Display.SafeText(_account.Error, _preferences.Current.PrivacyMode);
    public string Freshness
    {
        get
        {
            if (_account.IsRefreshing) return Loc.T("Actualisation en cours…");
            if (_account.Snapshot is null) return HasError ? Error : Loc.F("Ouvrez ce compte dans {0} pour détecter ses quotas.", ProviderName);
            return IsActive ? Loc.F("Mis à jour {0}", Display.Age(_account.Snapshot.FetchedAt)) : Loc.F("Dernier relevé {0} · quota figé tant que ce compte est inactif", Display.Age(_account.Snapshot.FetchedAt));
        }
    }
    public string AllDetails
    {
        get
        {
            var lines = new List<string> { Email, Loc.F("Offre : {0}", PlanBadge), SubscriptionDetails, "", Freshness, "" };
            if (HasResetEstimate || HasManualReset) { lines.Add(EstimateHint); lines.Add(""); }
            foreach (var bucket in _account.Snapshot?.Buckets ?? [])
            {
                lines.Add(Display.SafeText(bucket.Name ?? bucket.Id, _preferences.Current.PrivacyMode));
                foreach (var window in bucket.Windows) lines.Add(Loc.F("{0} : {1} restant\nReset : {2}\n{3} · {4}",
                    Display.Duration(window.WindowDurationMins), Display.Percent(window.RemainingPercent), Display.Exact(window.ResetsAt), Display.Zone(window.ResetsAt), Display.Countdown(window.ResetsAt)));
                lines.Add("");
            }
            lines.Add(ReserveSummary); lines.Add(CreditDetails);
            if (HasError) lines.Add(Error);
            return string.Join("\n", lines);
        }
    }
}

internal sealed class DashboardViewModel : INotifyPropertyChanged
{
    private TrackerState _state = new([], null);
    private readonly PreferencesStore _preferences;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    /// <summary>Pace estimate for active accounts; absent in isolated view-model tests.</summary>
    internal Func<Guid, UsageForecast>? Forecast { get; set; }
    public DashboardViewModel(bool isDemo, PreferencesStore preferences) { IsDemo = isDemo; _preferences = preferences; Advice = AccountAdvisor.Evaluate(_state, PreviewClock.UtcNow); }
    public bool IsDemo { get; }
    public AccountViewModel? Active => Accounts.FirstOrDefault(a => a.IsActive);
    public IReadOnlyList<AccountViewModel> ActiveAccounts => Accounts.Where(a => a.IsActive).ToArray();
    public bool HasActive => Active is not null;
    public bool IsEmpty => Accounts.Count == 0;
    public string AccountCount => Accounts.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasManualReset => Accounts.Any(a => a.HasManualReset);
    public string ManualResetCaption => _preferences.Current.ManualCodexReset is { } reset ? Loc.F("Reset Codex déclaré le {0} · {1} compte(s) en attente d’un nouveau relevé", Display.Exact(reset.At), Accounts.Count(a => a.HasManualReset)) : "";
    public string ManualResetHint => string.Join("\n\n", Accounts.Where(a => a.HasManualReset).Select(a => a.ManualResetHint));
    public bool HasExpectedResets => Accounts.Any(a => a.HasResetEstimate);
    public bool HasGlobalReset => Accounts.Any(a => a.GlobalAnnouncement is not null);
    public string ExpectedResetsCaption => HasGlobalReset ? Loc.T("Reset général annoncé · estimation datée et sources dans Resets") : Loc.T("Reset prévu passé · estimation à confirmer dans le compte concerné");
    public string ExpectedResetsTitle
    {
        get
        {
            var count = Accounts.Count(a => a.HasResetEstimate);
            return count == 1 ? Loc.T("Un autre compte est probablement rechargé") : Loc.F("{0} autres comptes sont probablement rechargés", count);
        }
    }
    public string ExpectedResetsNames => string.Join(" · ", Accounts.Where(a => a.HasResetEstimate).Select(a => a.Email));
    public string ExpectedResetsHint => string.Join("\n\n", Accounts.Where(a => a.HasResetEstimate).Select(a => $"{a.Email}\n{a.EstimateHint}"));
    public bool IsIdle => !_state.IsBusy;
    public bool IsBusy => _state.IsBusy;
    public bool ShowOnboarding => !_state.OnboardingComplete && !IsDemo;

    public Brush StatusBrush => ThemeManager.GetBrush(_state.IsBusy ? "WarningBrush" : _state.ActiveAccounts.Any(a => a.Error is not null) ? "DangerBrush"
        : _state.ActiveAccount?.Snapshot is not null ? "GoodBrush" : "MutedBrush");
    public string StatusText
    {
        get
        {
            if (_state.ActiveAccount?.Snapshot is not { } snapshot)
                return _state.IsBusy ? Loc.T("Première actualisation…") : _state.ActiveAccount is null ? Loc.T("En attente du compte actif") : Loc.T("Aucun relevé reçu");
            var at = snapshot.FetchedAt.ToLocalTime();
            var date = at.ToString(at.Date == DateTimeOffset.Now.Date ? "HH:mm:ss" : "dd/MM/yyyy HH:mm:ss", Loc.Culture);
            var updated = Loc.F("Dernière mise à jour : {0}", date);
            return _state.IsBusy ? Loc.F("{0} · actualisation…", updated) : _state.ActiveAccounts.Any(a => a.Error is not null) ? Loc.F("{0} · échec de lecture des quotas", updated) : updated;
        }
    }
    public string StatusHint => _state.ActiveAccounts.Count == 0 ? _state.StatusMessage ?? Loc.T("Ouvrez votre compte dans Codex ou Claude Code.")
        : string.Join("\n\n", _state.ActiveAccounts.Select(a => Loc.F("{0} · dernier relevé reçu : {1}\n{2}\n{3}", a.Profile.ProviderName, Display.Exact(a.Snapshot?.FetchedAt), Display.Zone(a.Snapshot?.FetchedAt), a.Error ?? _state.StatusMessage)));
    private AccountAdvice Advice { get; set; }
    public Guid? AdviceAccountId => Advice.Kind == AccountAdviceKind.VerifyInCodex ? Advice.AccountId : null;
    public bool HasAdvice => AdviceAccountId is not null;
    public string AdviceTitle
    {
        get
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Profile.Id == AdviceAccountId);
            return account is null ? "" : Loc.F("À vérifier dans Codex : {0}", PrivacyText.Account(account.Profile, _state, _preferences.Current));
        }
    }
    public string AdviceAge => Advice.ObservedAt is { } at ? Loc.F("Relevé {0} · quota actuel à confirmer", Display.Age(at)) : "";
    public string AdviceHint => Advice.Reason + "\n" + Loc.F("Relevé : {0}\n{1}", Display.Exact(Advice.ObservedAt), Display.Zone(Advice.ObservedAt));
    public void Tick()
    {
        foreach (var account in Accounts) account.Tick();
        Advice = AccountAdvisor.Evaluate(_state, PreviewClock.UtcNow);
        foreach (var property in new[] { nameof(HasAdvice), nameof(AdviceTitle), nameof(AdviceAge), nameof(AdviceHint), nameof(AdviceAccountId), nameof(StatusText), nameof(StatusHint), nameof(HasExpectedResets), nameof(ExpectedResetsTitle), nameof(ExpectedResetsNames), nameof(ExpectedResetsHint), nameof(HasGlobalReset), nameof(ExpectedResetsCaption), nameof(HasManualReset), nameof(ManualResetCaption), nameof(ManualResetHint) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
    public void Update(TrackerState state)
    {
        _state = state = state with { ManualCodexReset = _preferences.Current.ManualCodexReset };
        Advice = AccountAdvisor.Evaluate(state, PreviewClock.UtcNow);
        var source = state.Accounts.Select((account, index) => (account, index));
        var sorted = source.OrderBy(a => a.account.Profile.Provider).ThenByDescending(a => a.account.IsActive).ThenBy(a => a.index);
        var ordered = sorted.Select(a => a.account).ToArray();
        var existing = Accounts.ToDictionary(a => a.Id);
        var next = ordered.Select(a => { if (existing.TryGetValue(a.Profile.Id, out var vm)) { vm.Update(a, state); return vm; } return new AccountViewModel(a, state, _preferences, Forecast); }).ToArray();
        foreach (var group in next.GroupBy(a => a.ProviderName))
        {
            var count = group.Count(); var index = 0;
            foreach (var account in group) { account.SetProviderHeader(index == 0, count, index == count - 1); index++; }
        }
        if (!Accounts.Select(a => a.Id).SequenceEqual(next.Select(a => a.Id))) { Accounts.Clear(); foreach (var vm in next) Accounts.Add(vm); }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
