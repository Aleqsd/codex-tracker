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
        var left = date.Value - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "Reset attendu";
        if (left.TotalDays >= 1) return $"Dans {(int)left.TotalDays} j {left.Hours:00} h";
        if (left.TotalHours >= 1) return $"Dans {(int)left.TotalHours} h {left.Minutes:00} min";
        return $"Dans {left.Minutes} min {left.Seconds:00} s";
    }
    public static string Duration(int? minutes) => minutes switch { null => "Fenêtre inconnue", 10080 => "Semaine", 300 => "5 heures", >= 1440 => $"{minutes / 1440.0:0.#} jours", >= 60 => $"{minutes / 60.0:0.#} heures", _ => $"{minutes} min" };
    public static string Age(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.UtcNow - timestamp;
        return age.TotalMinutes < 1 ? "à l’instant" : age.TotalHours < 1 ? $"il y a {(int)age.TotalMinutes} min" : age.TotalDays < 1 ? $"il y a {(int)age.TotalHours} h" : $"il y a {(int)age.TotalDays} j";
    }
    public static string SafeText(string? text, bool privacy) => privacy ? Regex.Replace(text ?? "", @"[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}.\-]+", "[compte masqué]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : text ?? "";
}

internal sealed record SortChoice(SortMode Value, string Label) { public override string ToString() => Label; }
internal sealed class AccountViewModel : INotifyPropertyChanged
{
    private AccountState _account;
    private TrackerState _state;
    private readonly PreferencesStore _preferences;
    public event PropertyChangedEventHandler? PropertyChanged;
    public AccountViewModel(AccountState account, TrackerState state, PreferencesStore preferences) { _account = account; _state = state; _preferences = preferences; }
    public void Update(AccountState account, TrackerState state) { _account = account; _state = state; Tick(); }
    public void Tick() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public Guid Id => _account.Profile.Id;
    public string Email => PrivacyText.Account(_account.Profile, _state, _preferences.Current.PrivacyMode);
    public string Initials => _preferences.Current.PrivacyMode ? Email.Replace("Compte ", "") : new string(_account.Profile.Email.Split('@')[0].Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])).ToArray());
    public bool IsActive => _account.IsActiveInCodex;
    public bool IsSelected => Id == _state.SelectedAccountId;
    public bool IsIdle => !_state.IsBusy;
    public bool CanSelect => !IsSelected && IsIdle;
    public bool CanRemove => IsIdle && !IsActive;
    public string SelectGlyph => IsSelected ? "●" : "○";
    public string SelectButtonText => IsSelected ? "Dans l’icône" : "Afficher dans l’icône";
    public string Plan => _account.Snapshot?.PlanType?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", string other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other), _ => _account.IsConnected ? "Offre inconnue" : "À détecter" };
    public string PlanBadge => _account.Snapshot?.PlanMultiplier is int multiplier ? $"{Plan} {multiplier}×" : Plan;
    public string CompactStatus => HasError ? "à vérifier" : IsActive ? "actif" : _account.Snapshot is not null ? Display.Age(_account.Snapshot.FetchedAt) : "à détecter";
    public string RowSubtitle => $"{PlanBadge} · {CompactStatus}";
    public string SummaryLabel => IsActive ? "Compte actif dans Codex" : "Dernier relevé disponible";
    public string WeeklyNumber => Display.Percent(_account.Snapshot?.Weekly?.RemainingPercent);
    public double WeeklyPercent => _account.Snapshot?.Weekly?.RemainingPercent ?? 0;
    public Brush WeeklyBrush => Display.QuotaBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public Brush CardBorder => ThemeManager.GetBrush(IsSelected ? "FocusBrush" : "LineBrush");
    public Brush RowBackground => ThemeManager.GetBrush(IsSelected ? "PanelBrush" : "BackgroundBrush");
    public string ShortWindowRemaining => Display.Percent(_account.Snapshot?.Short?.RemainingPercent);
    public string ShortSummary => $"5 h : {ShortWindowRemaining}";
    public string ResetExact => Display.Exact(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetZone => Display.Zone(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCountdown => Display.Countdown(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCompact => ResetCountdown.Replace("Dans ", "");
    public string ResetHint => $"{ResetExact}\n{ResetZone}";
    public string SummaryReset => $"Reset hebdomadaire {ResetCountdown.ToLowerInvariant()}";
    public string ReserveCount => _account.Snapshot?.AvailableResetCredits?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ReserveSummary => _account.Snapshot?.AvailableResetCredits is int count ? $"{count} reset{(count == 1 ? "" : "s")} en réserve" : "Réserve indisponible";
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
    public string CreditDetails => _account.Snapshot?.ResetCredits is { Count: > 0 } credits ? string.Join("\n", credits.Select(c => $"{Display.SafeText(c.Title ?? "Crédit", _preferences.Current.PrivacyMode)} · expire le {Display.Exact(c.ExpiresAt)}\n{Display.Zone(c.ExpiresAt)}")) : "Aucune expiration communiquée par Codex.";
    public bool HasError => !string.IsNullOrWhiteSpace(_account.Error);
    public string Error => Display.SafeText(_account.Error, _preferences.Current.PrivacyMode);
    public string Freshness
    {
        get
        {
            if (_account.IsRefreshing) return "Actualisation en cours…";
            if (_account.Snapshot is null) return "Ouvrez ce compte dans Codex pour détecter ses quotas.";
            return IsActive ? $"Mis à jour {Display.Age(_account.Snapshot.FetchedAt)}" : $"Dernier relevé {Display.Age(_account.Snapshot.FetchedAt)} · quota figé tant que ce compte est inactif";
        }
    }
    public string AllDetails
    {
        get
        {
            var lines = new List<string> { Email, $"Offre : {PlanBadge}", SubscriptionDetails, "", Freshness, "" };
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
    public IReadOnlyList<SortChoice> SortChoices { get; } = [new(SortMode.Active, "Compte actif"), new(SortMode.Quota, "Quota restant"), new(SortMode.Reset, "Prochain reset"), new(SortMode.Plan, "Type d’offre")];
    public DashboardViewModel(bool isDemo, PreferencesStore preferences) { IsDemo = isDemo; _preferences = preferences; }
    public bool IsDemo { get; }
    public AccountViewModel? Active => Accounts.FirstOrDefault(a => a.IsActive) ?? Accounts.FirstOrDefault(a => a.IsSelected) ?? Accounts.FirstOrDefault();
    public bool HasActive => Active is not null;
    public bool IsEmpty => Accounts.Count == 0;
    public string AccountCount => Accounts.Count.ToString(CultureInfo.InvariantCulture);
    public bool IsIdle => !_state.IsBusy;
    public bool ShowOnboarding => !_state.OnboardingComplete && !IsDemo;
    public bool IsPrivate => _preferences.Current.PrivacyMode;
    public string PrivacyLabel => IsPrivate ? "Identités masquées" : "Masquer les identités";
    public string PrivacyGlyph => IsPrivate ? "◉" : "◎";
    public Brush StatusBrush => ThemeManager.GetBrush(_state.IsBusy ? "WarningBrush" : "MutedBrush");
    public string StatusText => Display.SafeText(_state.StatusMessage ?? (_state.IsBusy ? "Actualisation…" : IsDemo ? "Démonstration · données fictives" : "Détection automatique · toutes les 2 secondes"), IsPrivate);
    public void Tick() { foreach (var account in Accounts) account.Tick(); }
    public void Update(TrackerState state)
    {
        _state = state;
        var source = state.Accounts.Select((account, index) => (account, index));
        var sorted = _preferences.Current.SortMode switch
        {
            SortMode.Quota => source.OrderBy(a => a.account.Snapshot?.Weekly?.RemainingPercent ?? double.MaxValue).ThenBy(a => a.index),
            SortMode.Reset => source.OrderBy(a => a.account.Snapshot?.Weekly?.ResetsAt ?? DateTimeOffset.MaxValue).ThenBy(a => a.index),
            SortMode.Plan => source.OrderBy(a => a.account.Snapshot?.PlanType ?? "zzz", StringComparer.OrdinalIgnoreCase).ThenBy(a => a.index),
            _ => source.OrderByDescending(a => a.account.IsActiveInCodex).ThenBy(a => a.index)
        };
        var ordered = sorted.Select(a => a.account).ToArray();
        var existing = Accounts.ToDictionary(a => a.Id);
        var next = ordered.Select(a => { if (existing.TryGetValue(a.Profile.Id, out var vm)) { vm.Update(a, state); return vm; } return new AccountViewModel(a, state, _preferences); }).ToArray();
        if (!Accounts.Select(a => a.Id).SequenceEqual(next.Select(a => a.Id))) { Accounts.Clear(); foreach (var vm in next) Accounts.Add(vm); }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
