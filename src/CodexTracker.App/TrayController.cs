using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace CodexTracker.App;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private readonly MainWindow _window;
    private readonly ITrackerService _service;
    private readonly Func<Task> _exit;
    private readonly PreferencesStore _preferences;
    private readonly TrayPeekWindow _peek;
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

    public TrayController(MainWindow window, ITrackerService service, PreferencesStore preferences, Func<Task> exit)
    {
        _window = window; _service = service; _exit = exit; _preferences = preferences;
        _peek = new TrayPeekWindow(_window.ShowPanel);
        _tray = new Forms.NotifyIcon { Visible = false, Text = "Codex Tracker" };
        _tray.MouseClick += (_, e) =>
        {
            HidePeek();
            if (e.Button == Forms.MouseButtons.Left) _window.Dispatcher.Invoke(_window.ShowPanel);
        };
        _tray.MouseMove += (_, _) => OnTrayHover();
        _tray.BalloonTipClicked += (_, _) => _window.ShowPanel();
        _hoverDelay.Tick += (_, _) => ShowPeek();
        _presence.Tick += (_, _) => CheckPeekPresence();
        _notifications.Tick += (_, _) => ShowNotifications();
        _expiryTimer.Tick += (_, _) => ShowExpiryReminder();
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
        _window.Dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _updateQueued, 0); Update(); });
    }
    private void PreferencesChanged(object? sender, EventArgs e)
    {
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(() => PreferencesChanged(sender, e)); return; }
        if (_disposed) return;
        if (!_preferences.Current.HoverPreview) HidePeek();
        _menuKey = null; Update();
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
        HidePeek(); _tray.ContextMenuStrip?.Close(); _shellRecovery.Stop();
        _notifications.Stop(); _pendingNotifications.Clear();
    }
    internal void OnResume()
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        if (!_window.Dispatcher.CheckAccess()) { _window.Dispatcher.InvokeAsync(OnResume); return; }
        _suspended = false; HandleEnvironmentChanged(taskbarCreated: true);
    }
    private void Update()
    {
        if (_disposed) return;
        var state = _service.State;
        var account = state.SelectedAccount;
        double? remaining = account?.Snapshot?.Weekly?.RemainingPercent;
        string number = remaining is null ? "--" : ((int)Math.Floor(Math.Clamp(remaining.Value, 0, 100))).ToString();
        var dark = _window.Theme.IsDark;
        var color = remaining is null ? Color.FromArgb(140, 140, 140) : remaining > 20 ? (dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(40, 40, 40)) : remaining >= 10 ? (dark ? Color.FromArgb(199, 170, 117) : Color.FromArgb(147, 103, 30)) : (dark ? Color.FromArgb(207, 142, 142) : Color.FromArgb(178, 68, 68));
        string key = number + color.ToArgb() + dark;
        if (_iconKey != key)
        {
            var old = _icon; _icon = RenderIcon(number, color, dark); _tray.Icon = _icon; old?.Dispose(); _iconKey = key;
        }
        var text = account is null ? "Codex Tracker · en attente d’un compte Codex" : $"{PrivacyText.Account(account.Profile, state, _preferences.Current)}\nSemaine : {(remaining is null ? "indisponible" : number + "% restant")}{(!account.IsActiveInCodex ? " · dernier relevé" : account.IsStale ? " · données anciennes" : "")}\nReset : {Display.Exact(account.Snapshot?.Weekly?.ResetsAt)}";
        _tray.Text = _peek.IsVisible ? "" : text.Length <= 127 ? text : text[..124] + "…";
        _peek.Update(state, _preferences.Current);
        var menuKey = state.SelectedAccountId + "/" + _preferences.Current.PrivacyMode + "/" + dark + "/" + string.Join("|", state.Accounts.Select(a => a.Profile.Id + ":" + a.IsActiveInCodex + ":" + a.Profile.Email));
        if (menuKey == _menuKey || _tray.ContextMenuStrip?.Visible == true) return;
        _menuKey = menuKey;
        var background = dark ? Color.FromArgb(36, 36, 36) : Color.FromArgb(249, 249, 248);
        var foreground = dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(35, 35, 35);
        var menu = new Forms.ContextMenuStrip { BackColor = background, ForeColor = foreground, ShowImageMargin = false, Renderer = new DarkMenuRenderer(dark) };
        menu.Opening += (_, _) => HidePeek();
        menu.Closed += (_, _) => _window.Dispatcher.InvokeAsync(Update);
        menu.Items.Add("Ouvrir le suivi", null, (_, _) => _window.ShowPanel());
        var accountsMenu = new Forms.ToolStripMenuItem("Compte dans l’icône") { BackColor = background, ForeColor = foreground };
        accountsMenu.DropDown.BackColor = background; accountsMenu.DropDown.ForeColor = foreground;
        accountsMenu.DropDown.Renderer = menu.Renderer;
        foreach (var item in state.Accounts)
        {
            var label = PrivacyText.Account(item.Profile, state, _preferences.Current) + (item.IsActiveInCodex ? " · actif" : "");
            var accountItem = new Forms.ToolStripMenuItem(label) { Checked = item.Profile.Id == state.SelectedAccountId };
            accountItem.Click += async (_, _) => await SafeAsync(() => _service.SelectAccountAsync(item.Profile.Id));
            accountsMenu.DropDownItems.Add(accountItem);
        }
        menu.Items.Add(accountsMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var privacy = new Forms.ToolStripMenuItem("Masquer les adresses") { Checked = _preferences.Current.PrivacyMode };
        privacy.Click += async (_, _) => await SafeAsync(() => { _preferences.Update(p => p with { PrivacyMode = !p.PrivacyMode }); return Task.CompletedTask; });
        menu.Items.Add(privacy);
        menu.Items.Add("Actualiser", null, async (_, _) => await _window.RefreshAsync());
        menu.Items.Add("Réglages", null, (_, _) => { _window.ShowPanel(); _window.ShowSettings(); });
        menu.Items.Add("Quitter", null, async (_, _) => await _exit());
        var oldMenu = _tray.ContextMenuStrip; _tray.ContextMenuStrip = menu; oldMenu?.Dispose();
    }
    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { _window.ShowPanel(); _window.ShowMessage("Action impossible", error.Message); }
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
            previous.Kind == NotificationKind.Reset || notification.Threshold < previous.Threshold)
            _pendingNotifications[key] = notification;
        if (!_notifications.IsEnabled) _notifications.Start();
    });

    private void ShowExpiryReminder()
    {
        if (_disposed || _suspended || _notifications.IsEnabled || _service.State.IsBusy || !_preferences.Current.ExpiryNotifications) return;
        var now = DateTimeOffset.UtcNow; var state = _service.State;
        var due = ExpiryReminders.Due(state, now, _preferences.Current.ExpiryLeadHours, _preferences.Current.SentExpiryReminders);
        if (due.Count == 0) return;
        var group = due.Where(r => r.AccountId == due[0].AccountId).ToArray();
        var account = state.Accounts.First(a => a.Profile.Id == group[0].AccountId);
        try
        {
            // Persist before showing so a restart does not repeat the reminder.
            _preferences.Update(p =>
            {
                var sent = p.SentExpiryReminders.Where(kv => kv.Value > now).ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var reminder in group) sent[reminder.Key] = reminder.ExpiresAt;
                return p with { SentExpiryReminders = sent };
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var name = PrivacyText.Account(account.Profile, state, true);
        _tray.ShowBalloonTip(8000, "Réserve bientôt expirée",
            $"{name} · {group.Length} échéance(s) de réserve\nExpiration : {Display.Exact(group[0].ExpiresAt)}\nRelevé : {Display.Exact(group[0].ObservedAt)}\nVérifiez leur disponibilité dans Codex.", Forms.ToolTipIcon.Info);
    }

    private void ShowNotifications()
    {
        _notifications.Stop();
        if (_disposed || _suspended) return;
        var pending = _pendingNotifications.Values.Where(n => NotificationPolicy.IsEnabled(n, _preferences.Current)).ToArray();
        _pendingNotifications.Clear();
        if (pending.Length == 0) return;
        var latest = pending.OrderByDescending(n => n.Kind == NotificationKind.Threshold).ThenBy(n => n.Threshold ?? 100).First();
        var (title, body) = NotificationPolicy.Compose(latest, _service.State);
        if (pending.Length > 1) body += $"\n{pending.Length - 1} autre événement dans le suivi.";
        _tray.ShowBalloonTip(5000, title, body, latest.Kind == NotificationKind.Reset ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    internal void SavePeekScreenshot(string path, double dpi = 96)
    {
        _peek.Update(_service.State, _preferences.Current);
        _peek.ShowNear(Forms.Cursor.Position);
        _peek.SaveScreenshot(path, dpi);
        _peek.Hide();
    }

    internal void ShowTestNotification()
    {
        if (!_disposed) _tray.ShowBalloonTip(5000, "Codex Tracker", "Les alertes de quota et de reset apparaîtront ici.", Forms.ToolTipIcon.Info);
    }
    internal static Icon CreateIcon(string number, Color color)
        => RenderIcon(number, color, true);

    private static Icon RenderIcon(string number, Color color, bool dark)
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        // Transparent like the Windows system icons; reserve the full width for the number.
        var known = int.TryParse(number, out var remaining);
        using var track = new Pen(dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(155, 155, 155), 4f);
        if (known) graphics.DrawLine(track, 6, 58, 58, 58);
        if (known && remaining > 0)
        {
            using var progress = new Pen(color, 4f);
            graphics.DrawLine(progress, 6, 58, 6 + 52 * Math.Clamp(remaining, 0, 100) / 100f, 58);
        }
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = StringAlignment.Center; format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        string label = known ? number : "—";
        float fontSize = label.Length == 3 ? 32 : 44;
        while (fontSize > 20)
        {
            using var candidate = new Font("Segoe UI Semibold", fontSize, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
            if (graphics.MeasureString(label, candidate, int.MaxValue, format).Width <= 58) break;
            fontSize--;
        }
        using var font = new Font("Segoe UI Semibold", fontSize, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(known ? (dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(35, 35, 35)) : color);
        graphics.DrawString(label, font, foreground, new RectangleF(0, -5, 64, 60), format);
        IntPtr handle = bitmap.GetHicon();
        try { using var unmanaged = Icon.FromHandle(handle); return (Icon)unmanaged.Clone(); }
        finally { DestroyIcon(handle); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _service.Changed -= Changed; _service.Notification -= Notified;
        _preferences.Changed -= PreferencesChanged; _window.Theme.Changed -= Changed;
        _hoverDelay.Stop(); _presence.Stop(); _notifications.Stop(); _shellRecovery.Stop(); _expiryTimer.Stop(); _pendingNotifications.Clear(); _peek.Close();
        _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _icon?.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer(bool dark) : base(new DarkColors(dark)) { }
    }
    private sealed class DarkColors(bool dark) : Forms.ProfessionalColorTable
    {
        public override Color MenuItemSelected => dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(231, 231, 229);
        public override Color MenuItemBorder => dark ? Color.FromArgb(85, 85, 85) : Color.FromArgb(204, 204, 200);
        public override Color ToolStripDropDownBackground => dark ? Color.FromArgb(36, 36, 36) : Color.FromArgb(249, 249, 248);
        public override Color MenuBorder => dark ? Color.FromArgb(65, 65, 65) : Color.FromArgb(215, 215, 212);
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    }
}
