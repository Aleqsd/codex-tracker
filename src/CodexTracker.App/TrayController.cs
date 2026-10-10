using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;

namespace CodexTracker.App;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private readonly WindowsNotificationDelivery _desktop;
    private readonly MainWindow _window;
    private readonly ITrackerService _service;
    private readonly Func<Task> _exit;
    private readonly PreferencesStore _preferences;
    private readonly TrayPeekWindow _peek;
    private readonly AppTypography.MenuFont _menuFont = new();
    private readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _presence = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _notifications = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly DispatcherTimer _shellRecovery = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<(Guid, UsageWindowKind), QuotaNotification> _pendingNotifications = new();
    private System.Drawing.Point _hoverPoint;
    private DateTimeOffset? _leftPeekAt;
    private DateTimeOffset _peekUpdatedAt;
    private string? _menuKey;
    private int _updateQueued;
    private Icon? _icon;
    private string? _iconKey;
    private bool _disposed, _suspended;
    private string _notificationPage = "Resets";

    public TrayController(MainWindow window, ITrackerService service, PreferencesStore preferences, Func<Task> exit)
    {
        _window = window; _service = service; _exit = exit; _preferences = preferences;
        _peek = new TrayPeekWindow(_window.ShowPanel);
        _tray = new Forms.NotifyIcon { Visible = false, Text = "Codex Tracker" };
        _desktop = new(ShowDesktop, () => !_disposed && !_suspended && _tray.Visible);
        if (service is not DemoTrackerService)
            WindowsToasts.Listen(page => _window.Dispatcher.InvokeAsync(() => { if (!_disposed) _window.OpenPage(page); }));
        _tray.MouseClick += (_, e) =>
        {
            HidePeek();
            if (e.Button == Forms.MouseButtons.Left) _window.Dispatcher.Invoke(_window.ShowPanel);
        };
        _tray.MouseMove += (_, _) => OnTrayHover();
        _tray.BalloonTipClicked += (_, _) => _window.ShowResets();
        _hoverDelay.Tick += (_, _) => ShowPeek();
        _presence.Tick += (_, _) => CheckPeekPresence();
        _notifications.Tick += (_, _) => ShowNotifications();
        _expiryTimer.Tick += async (_, _) => await CheckRemindersAsync();
        _window.Reminders.ShowWindows = rows => _window.Dispatcher.CheckAccess()
            ? ShowReminders(rows) : _window.Dispatcher.Invoke(() => ShowReminders(rows));
        if (service is not DemoTrackerService) _expiryTimer.Start();
        _shellRecovery.Tick += (_, _) =>
        {
            _shellRecovery.Stop();
            if (_disposed || _suspended) return;
            // NotifyIcon already processes TaskbarCreated. Reassert the same icon ID
            // after Explorer settles, covering a shell which was not ready on broadcast.
            _tray.Visible = false; _tray.Visible = true; Update();
        };
        service.Changed += Changed;
        service.Notification += Notified;
        preferences.Changed += PreferencesChanged;
        window.Theme.Changed += Changed;
        Update(); _tray.Visible = true;
    }
    private void Changed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _updateQueued, 1) != 0) return;
        _window.Dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _updateQueued, 0); Update(); _ = CheckRemindersAsync(); });
    }
    private void PreferencesChanged(object? sender, EventArgs e)
    {
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(() => PreferencesChanged(sender, e)); return; }
        if (_disposed) return;
        if (!_preferences.Current.HoverPreview) HidePeek();
        _menuKey = null; Update(); _ = CheckRemindersAsync();
    }
    internal void HandleEnvironmentChanged(bool taskbarCreated = false)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(() => HandleEnvironmentChanged(taskbarCreated)); return; }
        HidePeek(); _tray.ContextMenuStrip?.Close();
        _menuKey = null; Update();
        if (taskbarCreated && !_suspended) { _shellRecovery.Stop(); _shellRecovery.Start(); }
    }
    internal void OnSuspend()
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(OnSuspend); return; }
        _suspended = true;
        _window.Reminders.Pause();
        HidePeek(); _tray.ContextMenuStrip?.Close(); _shellRecovery.Stop();
        _notifications.Stop(); _pendingNotifications.Clear();
    }
    internal void OnResume()
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(OnResume); return; }
        _suspended = false; _window.Reminders.Resume(); HandleEnvironmentChanged(taskbarCreated: true); _ = CheckRemindersAsync();
    }
    private void Update()
    {
        if (_disposed) return;
        var state = _service.State with { ManualCodexReset = _preferences.Current.ManualCodexReset };
        var account = state.ActiveAccount;
        double? remaining = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Weekly, PreviewClock.UtcNow);
        string number = remaining is null ? "--" : ((int)Math.Floor(Math.Clamp(remaining.Value, 0, 100))).ToString();
        var dark = _window.Theme.IsDark;
        var color = remaining is null ? Color.FromArgb(140, 140, 140) : remaining > 20 ? (dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(40, 40, 40)) : remaining >= 10 ? (dark ? Color.FromArgb(199, 170, 117) : Color.FromArgb(147, 103, 30)) : (dark ? Color.FromArgb(207, 142, 142) : Color.FromArgb(178, 68, 68));
        int iconSize = WindowsLifecycle.TrayIconPixelSize();
        string key = $"{number}/{color.ToArgb()}/{dark}/{iconSize}";
        if (_iconKey != key)
        {
            var old = _icon; _icon = TrayIconRenderer.Render(number, color, dark, iconSize); _tray.Icon = _icon; old?.Dispose(); _iconKey = key;
        }
        string text;
        if (account is null) text = Loc.T("Codex Tracker · en attente d’un compte");
        else
        {
            var weekly = remaining is null ? Loc.T("indisponible") : Loc.F("{0}% restant", number);
            if (state.ManualCodexReset?.Applies(account, ResetKind.Weekly, PreviewClock.UtcNow) == true) weekly = Loc.F("{0} · déclaré", weekly);
            if (!account.IsActive) weekly = Loc.F("{0} · dernier relevé", weekly);
            else if (account.IsStale) weekly = Loc.F("{0} · données anciennes", weekly);
            text = Loc.F("{0} · {1}\nSemaine : {2}\nReset : {3}", account.Profile.ProviderName, PrivacyText.ContextualAccount(account.Profile, state, _preferences.Current),
                weekly, Display.Exact(QuotaPresentation.ResetsAt(state, account, ResetKind.Weekly, PreviewClock.UtcNow)));
        }
        _tray.Text = _peek.IsVisible ? "" : text.Length <= 127 ? text : text[..124] + "…";
        _peek.Update(state, _preferences.Current);
        var menuKey = state.ActiveAccount?.Profile.Id + "/" + _preferences.Current.PrivacyMode + "/" + dark + "/" + string.Join("|", state.Accounts.Select(a => a.Profile.Id + ":" + a.IsActive + ":" + a.Profile.Email));
        if (menuKey == _menuKey || _tray.ContextMenuStrip?.Visible == true) return;
        _menuKey = menuKey;
        var background = dark ? Color.FromArgb(23, 24, 28) : Color.FromArgb(255, 255, 255);
        var foreground = dark ? Color.FromArgb(236, 237, 240) : Color.FromArgb(22, 23, 26);
        var menu = new Forms.ContextMenuStrip { Font = _menuFont.Font, BackColor = background, ForeColor = foreground, ShowImageMargin = false, Renderer = new DarkMenuRenderer(dark) };
        menu.Opening += (_, _) => HidePeek();
        menu.Closed += (_, _) => _window.Dispatcher.InvokeAsync(Update);
        menu.Items.Add(Loc.T("Ouvrir le suivi"), null, (_, _) => _window.ShowPanel());
        menu.Items.Add(Loc.T("Actualiser"), null, async (_, _) => await _window.RefreshAsync());
        menu.Items.Add(Loc.T("Réglages"), null, (_, _) => { _window.ShowPanel(); _window.ShowSettings(); });
        menu.Items.Add(Loc.T("Quitter"), null, async (_, _) => await _exit());
        var oldMenu = _tray.ContextMenuStrip; _tray.ContextMenuStrip = menu; oldMenu?.Dispose();
    }
    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { _window.ShowPanel(); _window.ShowMessage(Loc.T("Action impossible"), error.Message); }
    }

    private void OnTrayHover()
    {
        if (_disposed || _suspended || !_preferences.Current.HoverPreview || _tray.ContextMenuStrip?.Visible == true) return;
        _hoverPoint = Forms.Cursor.Position;
        _leftPeekAt = null;
        if (!_peek.IsVisible && !_hoverDelay.IsEnabled) _hoverDelay.Start();
    }

    private void ShowPeek()
    {
        _hoverDelay.Stop();
        if (_disposed || _suspended || !_preferences.Current.HoverPreview || _tray.ContextMenuStrip?.Visible == true) return;
        var cursor = Forms.Cursor.Position;
        if (Math.Abs(cursor.X - _hoverPoint.X) > 22 || Math.Abs(cursor.Y - _hoverPoint.Y) > 22) return;
        _peek.Update(_service.State, _preferences.Current);
        _peekUpdatedAt = DateTimeOffset.UtcNow;
        _peek.ShowNear(cursor);
        _tray.Text = "";
        _presence.Start();
    }

    private void CheckPeekPresence()
    {
        if (!_peek.IsVisible) { _presence.Stop(); Update(); return; }
        if (DateTimeOffset.UtcNow - _peekUpdatedAt >= TimeSpan.FromSeconds(1))
        {
            _peek.Update(_service.State, _preferences.Current);
            _peekUpdatedAt = DateTimeOffset.UtcNow;
        }
        var cursor = Forms.Cursor.Position;
        var nearTray = Math.Abs(cursor.X - _hoverPoint.X) <= 22 && Math.Abs(cursor.Y - _hoverPoint.Y) <= 22;
        if (nearTray || _peek.ContainsScreenPoint(cursor)) { _leftPeekAt = null; return; }
        _leftPeekAt ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - _leftPeekAt >= TimeSpan.FromMilliseconds(350)) HidePeek();
    }

    private void HidePeek()
    {
        _hoverDelay.Stop(); _presence.Stop(); _leftPeekAt = null;
        if (_peek.IsVisible) { _peek.Hide(); if (!_disposed) Update(); }
    }

    private void Notified(object? sender, QuotaNotification notification) => _window.Dispatcher.InvokeAsync(() =>
    {
        if (_disposed || _suspended || !NotificationPolicy.IsEnabled(notification, _preferences.Current)) return;
        var key = (notification.AccountId, notification.Window);
        if (!_pendingNotifications.TryGetValue(key, out var previous) || notification.Kind == NotificationKind.Reset ||
            previous.Kind is NotificationKind.Reset or NotificationKind.Forecast || notification.Threshold < previous.Threshold)
            _pendingNotifications[key] = notification;
        if (!_notifications.IsEnabled) _notifications.Start();
    });

    private async Task CheckRemindersAsync()
    {
        if (_disposed || _suspended || _notifications.IsEnabled || _service.State.IsBusy) return;
        await _window.Reminders.TickAsync();
    }

    private void ShowNotifications()
    {
        _notifications.Stop();
        if (_disposed || _suspended) return;
        var pending = _pendingNotifications.Values.Where(n => NotificationPolicy.IsEnabled(n, _preferences.Current)).ToArray();
        _pendingNotifications.Clear();
        if (pending.Length == 0) return;
        var latest = pending.OrderByDescending(n => n.Kind == NotificationKind.Threshold).ThenBy(n => n.Threshold ?? 100).First();
        var (title, body) = NotificationPolicy.Compose(latest, _service.State, _preferences.Current);
        var others = pending.Length - 1;
        if (others > 0) body += "\n" + (others == 1 ? Loc.T("1 autre événement dans le suivi.") : Loc.F("{0} autre événement dans le suivi.", others));
        _notificationPage = latest.Kind == NotificationKind.Reset ? "Resets" : "Comptes";
        _desktop.Send(title, body);
    }

    internal void SavePeekScreenshot(string path, double dpi = 96)
    {
        _peek.Update(_service.State, _preferences.Current);
        _peek.ShowNear(Forms.Cursor.Position);
        _peek.SaveScreenshot(path, dpi);
        _peek.Hide();
    }

    // Toasts carry an "open" action and a Windows-managed snooze; the classic balloon remains the fallback.
    private void ShowDesktop(string title, string body)
    {
        try { WindowsToasts.Show(title, body, _notificationPage); }
        catch (Exception) { _tray.ShowBalloonTip(8000, title, body, Forms.ToolTipIcon.Info); }
    }
    private DeliveryResult ShowReminders(IReadOnlyList<ReminderOccurrence> rows)
    {
        _notificationPage = "Resets";
        if (rows.Count == 0) return new(DeliveryStatus.Failed, Loc.T("Aucun rappel à transmettre à Windows."));
        if (rows.Count == 1 && rows[0].Key.StartsWith("test/", StringComparison.Ordinal))
            return _desktop.Send("Codex Tracker · Test", Loc.T("Les alertes de quota et de reset apparaîtront ici. Cliquez pour ouvrir l’onglet Resets."));
        var message = DesktopReminderText.For(rows);
        return _desktop.Send(message.Title, message.Body, deferWhenBusy: true);
    }
    internal static Icon CreateIcon(string number, Color color)
        => TrayIconRenderer.Render(number, color, true, WindowsLifecycle.TrayIconPixelSize());
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _service.Changed -= Changed; _service.Notification -= Notified;
        _window.Reminders.ShowWindows = null;
        _preferences.Changed -= PreferencesChanged; _window.Theme.Changed -= Changed;
        _hoverDelay.Stop(); _presence.Stop(); _notifications.Stop(); _shellRecovery.Stop(); _expiryTimer.Stop(); _pendingNotifications.Clear(); _peek.Close();
        _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _icon?.Dispose();
        _menuFont.Dispose();
    }
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer(bool dark) : base(new DarkColors(dark)) { }
    }
    private sealed class DarkColors(bool dark) : Forms.ProfessionalColorTable
    {
        public override Color MenuItemSelected => dark ? Color.FromArgb(38, 40, 48) : Color.FromArgb(236, 238, 242);
        public override Color MenuItemBorder => dark ? Color.FromArgb(38, 40, 48) : Color.FromArgb(236, 238, 242);
        public override Color ToolStripDropDownBackground => dark ? Color.FromArgb(23, 24, 28) : Color.FromArgb(255, 255, 255);
        public override Color MenuBorder => dark ? Color.FromArgb(39, 41, 48) : Color.FromArgb(225, 227, 232);
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    }
}
