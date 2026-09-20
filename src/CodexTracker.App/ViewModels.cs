using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodexTracker.App;

internal static class Display
{
    public static readonly Brush Green = Brush("#A4BEAD"), Orange = Brush("#C7AA75"), Red = Brush("#CF8E8E"), Muted = Brush("#A3A3A3");
    public static Brush Brush(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; b.Freeze(); return b; }
    public static Brush QuotaBrush(double? value) => value is null ? Muted : value > 20 ? Green : value >= 10 ? Orange : Red;
    public static string Percent(double? value) => value is null ? "—" : $"{Math.Floor(value.Value):0}%";
    public static string Exact(DateTimeOffset? date) => date?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("fr-FR")) ?? "Indisponible";
    public static string Zone(DateTimeOffset? date)
    {
        if (date is null) return "Date non communiquée";
        var localId = TimeZoneInfo.Local.Id;
        var zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(localId, out var iana) ? iana : localId;
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
}

internal sealed class AccountViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Tick() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private readonly AccountState _account;
    private readonly TrackerState _state;
    private QuotaWindow? ShortWindow => _account.Snapshot?.Buckets.FirstOrDefault(b => b.Id == "codex")?.Windows.Where(w => !w.IsWeekly).OrderBy(w => w.WindowDurationMins ?? int.MaxValue).FirstOrDefault();
    public AccountViewModel(AccountState account, TrackerState state) { _account = account; _state = state; }
    public Guid Id => _account.Profile.Id;
    public string Email => _account.Profile.Email;
    public string Initials => new string(Email.Split('@')[0].Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])).ToArray());
    public bool IsActive => _account.IsActiveInCodex;
    public bool IsSelected => Id == _state.SelectedAccountId;
    public bool IsIdle => !_state.IsBusy;
    public bool CanSelect => !IsSelected && IsIdle;
    public bool CanRemove => IsIdle && !IsActive;
    public string SelectButtonText => IsSelected ? "✓  Dans l’icône" : "Afficher dans l’icône";
    public string Plan => _account.Snapshot?.PlanType?.ToLowerInvariant() switch
    {
        "pro" or "prolite" => "PRO",
        "plus" => "PLUS",
        "free" => "FREE",
        string other => other.ToUpperInvariant(),
        _ => _account.IsConnected ? "OFFRE INDISPONIBLE" : "À DÉTECTER"
    };
    public string PlanBadge => _account.Snapshot?.PlanMultiplier is int multiplier ? $"{Plan} · {multiplier}×" : Plan;
    public Brush PlanForeground => Plan switch { "PRO" => Display.Brush("#D6D6D6"), "PLUS" => Display.Brush("#D6D6D6"), "FREE" => Display.Brush("#C3C3C3"), _ => Display.Muted };
    public Brush PlanBackground => Plan switch { "PRO" => Display.Brush("#353535"), "PLUS" => Display.Brush("#353535"), _ => Display.Brush("#353535") };
    public string SubscriptionSummary
    {
        get
        {
            var snapshot = _account.Snapshot;
            if (snapshot?.SubscriptionStartedAt is null && snapshot?.SubscriptionEndsAt is null) return "Période d’abonnement : indisponible";
            string start = snapshot?.SubscriptionStartedAt is DateTimeOffset begin ? begin.ToLocalTime().ToString("dd/MM/yyyy") : "début indisponible";
            string end = snapshot?.SubscriptionEndsAt is DateTimeOffset finish ? finish.ToLocalTime().ToString("dd/MM/yyyy") : "fin indisponible";
            return $"Période d’abonnement : {start} → {end}";
        }
    }
    public string SubscriptionDetails => $"Période d’abonnement active\nDébut : {Display.Exact(_account.Snapshot?.SubscriptionStartedAt)}\n{Display.Zone(_account.Snapshot?.SubscriptionStartedAt)}\nFin : {Display.Exact(_account.Snapshot?.SubscriptionEndsAt)}\n{Display.Zone(_account.Snapshot?.SubscriptionEndsAt)}\nDates de la période communiquée par Codex.";
    public string WeeklyNumber => Display.Percent(_account.Snapshot?.Weekly?.RemainingPercent);
    public double WeeklyPercent => _account.Snapshot?.Weekly?.RemainingPercent ?? 0;
    public Brush WeeklyBrush => Display.QuotaBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public Brush CardBorder => IsSelected ? Display.Brush("#606060") : Display.Brush("#373737");
    public Brush AvatarBackground => IsSelected ? Display.Brush("#3B3B3B") : Display.Brush("#303030");
    public Brush AvatarForeground => Display.Brush("#D3D3D3");
    public string ShortWindowLabel => ShortWindow is null ? "AUTRE FENÊTRE" : Display.Duration(ShortWindow.WindowDurationMins).ToUpperInvariant();
    public string ShortWindowRemaining => Display.Percent(ShortWindow?.RemainingPercent);
    public double ShortWindowPercent => ShortWindow?.RemainingPercent ?? 0;
    public string ResetExact => Display.Exact(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetZone => Display.Zone(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCountdown => Display.Countdown(_account.Snapshot?.Weekly?.ResetsAt);
    public string ResetCountdownWithZone => $"{ResetCountdown} · {_account.Snapshot?.Weekly?.ResetsAt?.ToLocalTime().ToString("zzz") ?? "—"}";
    public string ReserveCount => _account.Snapshot?.AvailableResetCredits?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ReserveSummary => _account.Snapshot?.AvailableResetCredits is int n ? $"↺  {n} reset{(n == 1 ? "" : "s")} en réserve" : "↺  Réserve non communiquée";
    private DateTimeOffset? FirstExpiry => _account.Snapshot?.ResetCredits?.Where(c => c.ExpiresAt is not null).Select(c => c.ExpiresAt).Order().FirstOrDefault();
    public string CreditExpirySummary => FirstExpiry is null ? "Expiration non fournie" : $"Expire le {FirstExpiry.Value.ToLocalTime():dd/MM/yyyy}";
    public string CreditDetails => _account.Snapshot?.ResetCredits is { Count: > 0 } credits ? string.Join("\n", credits.Select(c => $"{c.Title ?? "Crédit"} · expire : {Display.Exact(c.ExpiresAt)} ({Display.Zone(c.ExpiresAt)})")) : "Aucune expiration communiquée par Codex.";
    public bool HasError => !string.IsNullOrWhiteSpace(_account.Error);
    public string Error => _account.Error ?? "";
    public Brush FreshnessBrush => HasError || (IsActive && _account.IsStale) ? Display.Orange : Display.Muted;
    public string Freshness
    {
        get
        {
            if (_account.IsRefreshing) return "Actualisation en cours…";
            if (_account.Snapshot is null) return _account.IsConnected ? "Compte détecté · en attente des quotas" : "Ouvrez ce compte dans Codex : il apparaîtra automatiquement ici";
            var age = DateTimeOffset.UtcNow - _account.Snapshot.FetchedAt;
            var text = age.TotalMinutes < 1 ? "à l’instant" : age.TotalMinutes < 60 ? $"il y a {(int)age.TotalMinutes} min" : $"le {Display.Exact(_account.Snapshot.FetchedAt)}";
            if (!IsActive) return $"Dernier relevé {text} · quota figé tant que ce compte est inactif";
            return $"{(_account.IsStale ? "Dernières données connues" : "Mis à jour")} {text}";
        }
    }
    public string AllDetails
    {
        get
        {
            var multiplier = _account.Snapshot?.PlanMultiplier is int value ? $"{value}×" : "non communiqué";
            var lines = new List<string> { Email, $"Offre : {Plan}", $"Multiplicateur : {multiplier}", SubscriptionDetails, "" };
            lines.Add(IsActive ? "Compte actif dans Codex · actualisation automatique." : "Compte inactif · les quotas affichés correspondent au dernier relevé. Ouvrez ce compte dans Codex pour les actualiser.");
            lines.Add("");
            foreach (var bucket in _account.Snapshot?.Buckets ?? [])
            {
                lines.Add(bucket.Name ?? bucket.Id);
                foreach (var window in bucket.Windows)
                    lines.Add($"  {Display.Duration(window.WindowDurationMins)} : {Display.Percent(window.RemainingPercent)} restant\n  Reset : {Display.Exact(window.ResetsAt)}\n  {Display.Zone(window.ResetsAt)} · {Display.Countdown(window.ResetsAt)}");
                lines.Add("");
            }
            lines.Add(ReserveSummary); lines.Add(CreditDetails);
            lines.Add(""); lines.Add(Freshness);
            if (HasError) lines.Add(Error);
            return string.Join("\n", lines);
        }
    }
}

internal sealed class DashboardViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    private TrackerState _state = new([], null);
    public bool IsDemo { get; }
    public DashboardViewModel(bool isDemo) => IsDemo = isDemo;
    public AccountViewModel? Selected => Accounts.FirstOrDefault(a => a.IsSelected);
    public bool HasSelected => Selected is not null;
    public bool IsEmpty => Accounts.Count == 0;
    public string AccountCount => Accounts.Count.ToString(CultureInfo.InvariantCulture);
    public bool IsIdle => !_state.IsBusy;
    public bool ShowOnboarding => !_state.OnboardingComplete && !IsDemo;
    public Brush StatusBrush => _state.IsBusy ? Display.Orange : Display.Green;
    public string StatusText => _state.StatusMessage ?? (_state.IsBusy ? "Lecture du compte Codex…" : $"{(IsDemo ? "Démonstration · " : "")}Détection automatique toutes les 2 secondes · quotas actifs toutes les 2 minutes");
    public void Tick() { foreach (var account in Accounts) account.Tick(); }
    public void Update(TrackerState state)
    {
        _state = state;
        Accounts.Clear();
        foreach (var a in state.Accounts) Accounts.Add(new AccountViewModel(a, state));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
