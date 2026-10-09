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
    public static string Exact(DateTimeOffset? date) => date?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("fr-FR")) ?? "Indisponible";
    public static string Zone(DateTimeOffset? date)
    {
        if (date is null) return "Date non communiquée";
        string local = TimeZoneInfo.Local.Id;
        string zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(local, out var iana) ? iana : local;
        return $"{zone} · UTC{date.Value.ToLocalTime():zzz}";
    }
    public static string Countdown(DateTimeOffset? date)
    {
        if (date is null) return "Indisponible";
        var left = date.Value - PreviewClock.UtcNow;
        if (left <= TimeSpan.Zero) return "Reset attendu";
        if (left.TotalDays >= 1) return $"Dans {(int)left.TotalDays} j {left.Hours:00} h";
        if (left.TotalHours >= 1) return $"Dans {(int)left.TotalHours} h {left.Minutes:00} min";
        return $"Dans {left.Minutes} min {left.Seconds:00} s";
    }
    public static string Duration(int? minutes) => minutes switch { null => "Fenêtre inconnue", 10080 => "Semaine", 300 => "5 heures", >= 1440 => $"{minutes / 1440.0:0.#} jours", >= 60 => $"{minutes / 60.0:0.#} heures", _ => $"{minutes} min" };
    public static string Age(DateTimeOffset timestamp)
    {
        var age = PreviewClock.UtcNow - timestamp;
        return age.TotalMinutes < 1 ? "à l’instant" : age.TotalHours < 1 ? $"il y a {(int)age.TotalMinutes} min" : age.TotalDays < 1 ? $"il y a {(int)age.TotalHours} h" : $"il y a {(int)age.TotalDays} j";
    }
    public static string SafeText(string? text, bool privacy) => privacy ? Regex.Replace(text ?? "", @"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+", "[compte masqué]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : text ?? "";
    public static string ReserveSummary(AccountSnapshot? snapshot) => snapshot?.AvailableResetCredits is int count
        ? $"{count} reset{(count == 1 ? "" : "s")} en réserve" : "Réserve indisponible";
    public static string CreditDetails(AccountSnapshot? snapshot, bool privacy) => snapshot?.ResetCredits is { Count: > 0 } credits
        ? string.Join("\n\n", credits.OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).Select((c, i) =>
            $"{SafeText(c.Title ?? $"Reset {i + 1}", privacy)}\n" +
            (c.GrantedAt is { } granted ? $"Reçu le {Exact(granted)} · {Zone(granted)}\n" : "") +
            (c.ExpiresAt is { } expires ? $"{(expires <= PreviewClock.UtcNow ? "Expiration passée" : "Expire le")} {Exact(expires)}\n{Zone(expires)}" : "Expiration non communiquée")))
        : "Aucune expiration communiquée par Codex.";
    public static string ReserveHint(AccountSnapshot? snapshot, bool privacy) =>
        $"{ReserveSummary(snapshot)}\n\n{CreditDetails(snapshot, privacy)}" +
        (snapshot is null ? "" : $"\n\nDernier relevé : {Exact(snapshot.FetchedAt)}\n{Zone(snapshot.FetchedAt)}\nDisponibilité au moment de ce relevé.");
}

internal sealed class AccountViewModel : INotifyPropertyChanged
{
    private AccountState _account;
    private TrackerState _state;
    private readonly PreferencesStore _preferences;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AccountViewModel(AccountState account, TrackerState state, PreferencesStore preferences) { _account = account; _state = state; _preferences = preferences; }
    public void Update(AccountState account, TrackerState state) { _account = account; _state = state; Tick(); }
    public void Tick() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private TrackerState PresentationState => _state with { ManualCodexReset = _preferences.Current.ManualCodexReset };
    public bool HasManualWeeklyReset => PresentationState.ManualCodexReset?.Applies(_account, ResetKind.Weekly, PreviewClock.UtcNow) == true;
    public bool HasManualShortReset => PresentationState.ManualCodexReset?.Applies(_account, ResetKind.Short, PreviewClock.UtcNow) == true;
    public bool HasManualReset => HasManualWeeklyReset || HasManualShortReset;
    public string WeeklyDisplayLabel => HasManualWeeklyReset ? "restant · déclaré" : "restant";
    public string ManualResetHint => !HasManualReset ? "" : $"Reset Codex déclaré pour le {Display.Exact(PresentationState.ManualCodexReset!.At)} · {Display.Zone(PresentationState.ManualCodexReset.At)}.\n100 % déclaré, sous réserve d’une utilisation depuis le reset.\nDernier quota mesuré : {Display.Percent(_account.Snapshot?.Weekly?.RemainingPercent)} le {Display.Exact(_account.Snapshot?.FetchedAt)}.\nLes prochains relevés mesurés sont prioritaires.";
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
        ShowProviderHeader = show; IsLastInSection = last; ProviderCount = count == 1 ? "1 compte" : $"{count} comptes"; Tick();
    }
    public bool HasReserves => _account.Profile.Provider == AccountProvider.Codex;
    public bool IsActive => _account.IsActive;
    public bool IsSelected => IsActive;
    public bool IsIdle => !_state.IsBusy;
    public bool CanRemove => IsIdle && !IsActive;
    public string Plan => (_account.Snapshot?.PlanType ?? _account.Profile.ProviderPlanType)?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", "team" => "Équipe", "enterprise" => "Entreprise", string other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other), _ => _account.IsConnected ? "Offre inconnue" : "À détecter" };
    public string PlanBadge => (_account.Snapshot?.PlanMultiplier ?? _account.Profile.ProviderPlanMultiplier) is int multiplier ? $"{Plan} {multiplier}×" : Plan;
    public string CompactStatus => HasError ? "à vérifier" : IsActive ? "actif" : _account.Snapshot is not null ? Display.Age(_account.Snapshot.FetchedAt) : "à détecter";
    public string Organization => _preferences.Current.PrivacyMode ? "" : _account.Profile.OrganizationName is { } name ? " · " + name : "";
    public string RowSubtitle => $"{PlanBadge}{Organization} · {CompactStatus}";
    public string SummaryLabel => IsActive ? $"Compte actif dans {ProviderName}{Organization}" : $"{ProviderName}{Organization} · dernier relevé disponible";
    private ExpectedReset? WeeklyEstimate => ExpectedReset.For(_account, ResetKind.Weekly, PreviewClock.UtcNow, _state.GlobalResetFeed, PresentationState.ManualCodexReset);
    private ExpectedReset? ShortEstimate => ExpectedReset.For(_account, ResetKind.Short, PreviewClock.UtcNow, _state.GlobalResetFeed, PresentationState.ManualCodexReset);
    public GlobalResetAnnouncement? GlobalAnnouncement => WeeklyEstimate?.Announcement ?? ShortEstimate?.Announcement;
    public bool HasWeeklyEstimate => WeeklyEstimate is not null;
    public bool HasResetEstimate => HasWeeklyEstimate || ShortEstimate is not null;
    public bool HasResetNotice => HasResetEstimate || HasManualReset;
    public string WeeklyDisplayNumber => HasManualWeeklyReset ? "100%" : HasWeeklyEstimate ? "≈100%" : WeeklyNumber;
    public Brush WeeklyDisplayBrush => HasManualWeeklyReset || HasWeeklyEstimate ? Display.Green : WeeklyBrush;
    public string EstimateLabel => HasManualReset ? "Reset Codex déclaré" : HasWeeklyEstimate && ShortEstimate is not null ? "Semaine + 5 h probablement à 100 %"
        : HasWeeklyEstimate ? "Semaine probablement à 100 %" : ShortEstimate is not null ? "5 h probablement à 100 %" : "";
    public string EstimateHint => HasManualReset ? ManualResetHint : string.Join("\n\n", new[] { WeeklyEstimate, ShortEstimate }.OfType<ExpectedReset>().Select(r =>
        (r.Announcement is { } a ? $"Reset général annoncé comme terminé le {Display.Exact(a.ReportedAt)} · {Display.Zone(a.ReportedAt)}.\nSource : {a.SourceUrl}\nPortée : {a.AnnouncementUrl}\nEstimation valable jusqu’au {Display.Exact(a.ReportedAt.AddHours(r.Kind == ResetKind.Short ? 5 : 24))}.\n" : $"{ReminderPlanner.Label(r.Kind)} prévu le {Display.Exact(r.At)} · {Display.Zone(r.At)}.\n") +
        $"Dernier quota mesuré : {Display.Percent(r.LastRemainingPercent)} le {Display.Exact(r.ObservedAt)}.\nProbablement revenu à 100 % si le compte n’a pas été utilisé ailleurs. Ouvrez ce compte dans {ProviderName} pour confirmer."));
    public string WeeklyHint => HasManualWeeklyReset ? ManualResetHint : HasWeeklyEstimate ? EstimateHint : $"Dernier quota mesuré : {WeeklyNumber}\nRelevé : {Display.Exact(_account.Snapshot?.FetchedAt)}";
    public string WeeklyNumber => Display.Percent(QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Weekly, PreviewClock.UtcNow));
    public double WeeklyPercent => QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Weekly, PreviewClock.UtcNow) ?? 0;
    public Brush WeeklyBrush => HasManualWeeklyReset ? Display.Green : Display.QuotaBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public Brush WeeklyBarBrush => HasManualWeeklyReset ? Display.Green : Display.QuotaBarBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public double WeeklyDisplayPercent => HasManualWeeklyReset || HasWeeklyEstimate ? 100 : WeeklyPercent;
    public Brush WeeklyDisplayBarBrush => HasManualWeeklyReset || HasWeeklyEstimate ? Display.Green : WeeklyBarBrush;
    public string WeeklyRingCaption => HasManualWeeklyReset ? "déclaré" : "semaine";
    public string ShortWindowRemaining => Display.Percent(QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Short, PreviewClock.UtcNow));
    public double ShortPercent => QuotaPresentation.Remaining(PresentationState, _account, ResetKind.Short, PreviewClock.UtcNow) ?? 0;
    public Brush ShortBarBrush => HasManualShortReset ? Display.Green : Display.QuotaBarBrush(_account.Snapshot?.Short?.RemainingPercent);
    public string ShortSummary => $"5 h : {ShortWindowRemaining}" + (HasManualShortReset ? " · déclaré" : "");
    public string ResetExact => Display.Exact(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetZone => Display.Zone(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCountdown => HasManualWeeklyReset ? "À reconfirmer" : Display.Countdown(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCompact => WeeklyEstimate?.Announcement is not null ? "À reconfirmer" : HasWeeklyEstimate ? "Reset passé" : ResetCountdown.Replace("Dans ", "");
    public string ResetHint => HasManualWeeklyReset ? ManualResetHint : HasWeeklyEstimate ? EstimateHint : $"{ResetExact}\n{ResetZone}";
    public string SummaryReset => $"Reset hebdomadaire {ResetCountdown.ToLowerInvariant()}";
    public string ReserveCount => _account.Snapshot?.AvailableResetCredits?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ReserveSummary => HasReserves ? Display.ReserveSummary(_account.Snapshot) : "Pas de resets en réserve";
    public string ReserveBadge => $"↺ {ReserveCount}";
    public Brush ReserveBrush => ThemeManager.GetBrush(_account.Snapshot?.AvailableResetCredits > 0 ? "TextBrush" : "MutedBrush");
    public string ReserveHint => HasReserves ? Display.ReserveHint(_account.Snapshot, _preferences.Current.PrivacyMode) : "Claude Code ne communique pas de resets en réserve.";
    public string SubscriptionSummary
    {
        get
        {
            if (_account.Snapshot?.SubscriptionStartedAt is null && _account.Snapshot?.SubscriptionEndsAt is null) return "Période indisponible";
            string start = _account.Snapshot?.SubscriptionStartedAt?.ToLocalTime().ToString("dd/MM/yyyy") ?? "début inconnu";
            string end = _account.Snapshot?.SubscriptionEndsAt?.ToLocalTime().ToString("dd/MM/yyyy") ?? "fin inconnue";
            return $"{start} → {end}";
        }
    }
    public string SubscriptionDetails => $"Période d’abonnement active\nDébut : {Display.Exact(_account.Snapshot?.SubscriptionStartedAt)}\n{Display.Zone(_account.Snapshot?.SubscriptionStartedAt)}\nFin : {Display.Exact(_account.Snapshot?.SubscriptionEndsAt)}\n{Display.Zone(_account.Snapshot?.SubscriptionEndsAt)}";
    public string CreditDetails => Display.CreditDetails(_account.Snapshot, _preferences.Current.PrivacyMode);
    public bool HasError => !string.IsNullOrWhiteSpace(_account.Error);
    public string Error => Display.SafeText(_account.Error, _preferences.Current.PrivacyMode);
    public string Freshness
    {
        get
        {
            if (_account.IsRefreshing) return "Actualisation en cours…";
            if (_account.Snapshot is null) return HasError ? Error : $"Ouvrez ce compte dans {ProviderName} pour détecter ses quotas.";
            return IsActive ? $"Mis à jour {Display.Age(_account.Snapshot.FetchedAt)}" : $"Dernier relevé {Display.Age(_account.Snapshot.FetchedAt)} · quota figé tant que ce compte est inactif";
        }
    }
    public string AllDetails
    {
        get
        {
            var lines = new List<string> { Email, $"Offre : {PlanBadge}", SubscriptionDetails, "", Freshness, "" };
            if (HasResetEstimate || HasManualReset) { lines.Add(EstimateHint); lines.Add(""); }
            foreach (var bucket in _account.Snapshot?.Buckets ?? [])
            {
                lines.Add(Display.SafeText(bucket.Name ?? bucket.Id, _preferences.Current.PrivacyMode));
                foreach (var window in bucket.Windows) lines.Add($"{Display.Duration(window.WindowDurationMins)} : {Display.Percent(window.RemainingPercent)} restant\nReset : {Display.Exact(window.ResetsAt)}\n{Display.Zone(window.ResetsAt)} · {Display.Countdown(window.ResetsAt)}");
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
    public DashboardViewModel(bool isDemo, PreferencesStore preferences) { IsDemo = isDemo; _preferences = preferences; Advice = AccountAdvisor.Evaluate(_state, PreviewClock.UtcNow); }
    public bool IsDemo { get; }
    public AccountViewModel? Active => Accounts.FirstOrDefault(a => a.IsActive);
    public IReadOnlyList<AccountViewModel> ActiveAccounts => Accounts.Where(a => a.IsActive).ToArray();
    public bool HasActive => Active is not null;
    public bool IsEmpty => Accounts.Count == 0;
    public string AccountCount => Accounts.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasManualReset => Accounts.Any(a => a.HasManualReset);
    public string ManualResetCaption => _preferences.Current.ManualCodexReset is { } reset ? $"Reset Codex déclaré le {Display.Exact(reset.At)} · {Accounts.Count(a => a.HasManualReset)} compte(s) en attente d’un nouveau relevé" : "";
    public string ManualResetHint => string.Join("\n\n", Accounts.Where(a => a.HasManualReset).Select(a => a.ManualResetHint));
    public bool HasExpectedResets => Accounts.Any(a => a.HasResetEstimate);
    public bool HasGlobalReset => Accounts.Any(a => a.GlobalAnnouncement is not null);
    public string ExpectedResetsCaption => HasGlobalReset ? "Reset général annoncé · estimation datée et sources dans Resets" : "Reset prévu passé · estimation à confirmer dans le compte concerné";
    public string ExpectedResetsTitle
    {
        get
        {
            var count = Accounts.Count(a => a.HasResetEstimate);
            return count == 1 ? "Un autre compte est probablement rechargé" : $"{count} autres comptes sont probablement rechargés";
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
                return _state.IsBusy ? "Première actualisation…" : _state.ActiveAccount is null ? "En attente du compte actif" : "Aucun relevé reçu";
            var at = snapshot.FetchedAt.ToLocalTime();
            var date = at.ToString(at.Date == DateTimeOffset.Now.Date ? "HH:mm:ss" : "dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("fr-FR"));
            return $"Dernière mise à jour : {date}" + (_state.IsBusy ? " · actualisation…" : _state.ActiveAccounts.Any(a => a.Error is not null) ? " · échec de lecture des quotas" : "");
        }
    }
    public string StatusHint => _state.ActiveAccounts.Count == 0 ? _state.StatusMessage ?? "Ouvrez votre compte dans Codex ou Claude Code."
        : string.Join("\n\n", _state.ActiveAccounts.Select(a => $"{a.Profile.ProviderName} · dernier relevé reçu : {Display.Exact(a.Snapshot?.FetchedAt)}\n{Display.Zone(a.Snapshot?.FetchedAt)}\n{a.Error ?? _state.StatusMessage}"));
    private AccountAdvice Advice { get; set; }
    public Guid? AdviceAccountId => Advice.Kind == AccountAdviceKind.VerifyInCodex ? Advice.AccountId : null;
    public bool HasAdvice => AdviceAccountId is not null;
    public string AdviceTitle
    {
        get
        {
            var account = _state.Accounts.FirstOrDefault(a => a.Profile.Id == AdviceAccountId);
            return account is null ? "" : "À vérifier dans Codex : " + PrivacyText.Account(account.Profile, _state, _preferences.Current);
        }
    }
    public string AdviceAge => Advice.ObservedAt is { } at ? $"Relevé {Display.Age(at)} · quota actuel à confirmer" : "";
    public string AdviceHint => $"{Advice.Reason}\nRelevé : {Display.Exact(Advice.ObservedAt)}\n{Display.Zone(Advice.ObservedAt)}";
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
        var next = ordered.Select(a => { if (existing.TryGetValue(a.Profile.Id, out var vm)) { vm.Update(a, state); return vm; } return new AccountViewModel(a, state, _preferences); }).ToArray();
        foreach (var group in next.GroupBy(a => a.ProviderName))
        {
            var count = group.Count(); var index = 0;
            foreach (var account in group) { account.SetProviderHeader(index == 0, count, index == count - 1); index++; }
        }
        if (!Accounts.Select(a => a.Id).SequenceEqual(next.Select(a => a.Id))) { Accounts.Clear(); foreach (var vm in next) Accounts.Add(vm); }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
