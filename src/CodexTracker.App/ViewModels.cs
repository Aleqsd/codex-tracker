using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CodexTracker.App;

internal static class Display
{
    public static readonly Brush Green = Brush("#66E0CB"), Orange = Brush("#FFC46C"), Red = Brush("#FF8191"), Muted = Brush("#92A4BD");
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
    public bool CanConnect => IsIdle;
    public bool CanSwitch => _state.CanSwitch && _account.IsConnected && !IsActive && IsIdle;
    public string SwitchHint => !_state.CanSwitch ? _state.SwitchUnavailableReason ?? "Bascule indisponible sur cette version de Codex." : IsActive ? "Ce compte est déjà actif dans Codex." : !_account.IsConnected ? "Connectez ce compte avant de l’utiliser." : "Codex sera fermé puis relancé après votre confirmation.";
    public string SelectButtonText => IsSelected ? "✓  Dans l’icône" : "Afficher dans l’icône";
    public string ConnectButtonText => _account.IsConnected ? "Reconnecter" : "Connecter";
    public string Plan => _account.Snapshot?.PlanType?.ToUpperInvariant() ?? (_account.IsConnected ? "OFFRE INDISPONIBLE" : "NON CONNECTÉ");
    public string WeeklyNumber => Display.Percent(_account.Snapshot?.Weekly?.RemainingPercent);
    public double WeeklyPercent => _account.Snapshot?.Weekly?.RemainingPercent ?? 0;
    public Brush WeeklyBrush => Display.QuotaBrush(_account.Snapshot?.Weekly?.RemainingPercent);
    public Brush CardBorder => IsSelected ? Display.Brush("#3D6B65") : Display.Brush("#253247");
    public Brush AvatarBackground => IsSelected ? Display.Brush("#21423F") : Display.Brush("#24314B");
    public Brush AvatarForeground => IsSelected ? Display.Green : Display.Brush("#AABFEB");
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
    public Brush FreshnessBrush => _account.IsStale ? Display.Orange : Display.Muted;
    public string Freshness
    {
        get
        {
            if (_account.IsRefreshing) return "Actualisation en cours…";
            if (_account.Snapshot is null) return _account.IsConnected ? "En attente des données" : "Connectez ce compte pour afficher ses quotas";
            var age = DateTimeOffset.UtcNow - _account.Snapshot.FetchedAt;
            var text = age.TotalMinutes < 1 ? "à l’instant" : age.TotalMinutes < 60 ? $"il y a {(int)age.TotalMinutes} min" : $"le {Display.Exact(_account.Snapshot.FetchedAt)}";
            return $"{(_account.IsStale ? "Dernières données connues" : "Mis à jour")} {text}";
        }
    }
    public string AllDetails
    {
        get
        {
            var lines = new List<string> { Email, $"Offre : {Plan}", "" };
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
    public bool HasPendingSwitch => _state.PendingSwitchEmail is not null;
    public string PendingSwitchMessage => $"Codex a été relancé. Vérifiez que {_state.PendingSwitchEmail} est affiché dans le menu du compte.";
    public Brush StatusBrush => _state.IsBusy ? Display.Orange : Display.Green;
    public string StatusText => _state.StatusMessage ?? (_state.IsBusy ? "Synchronisation en cours…" : $"{(IsDemo ? "Données de démonstration · " : "")}Actualisation automatique toutes les 2 minutes");
    public void Tick() { foreach (var account in Accounts) account.Tick(); }
    public void Update(TrackerState state)
    {
        _state = state;
        Accounts.Clear();
        foreach (var a in state.Accounts) Accounts.Add(new AccountViewModel(a, state));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
