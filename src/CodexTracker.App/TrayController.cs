using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Color = System.Drawing.Color;

namespace CodexTracker.App;

internal sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _tray;
    private readonly MainWindow _window;
    private readonly ITrackerService _service;
    private readonly Func<Task> _exit;
    private Icon? _icon;
    private string? _iconKey;
    private bool _disposed;

    public TrayController(MainWindow window, ITrackerService service, Func<Task> exit)
    {
        _window = window; _service = service; _exit = exit;
        _tray = new Forms.NotifyIcon { Visible = false, Text = "Codex Tracker" };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _window.Dispatcher.Invoke(_window.ShowPanel); };
        service.Changed += Changed;
        Update(); _tray.Visible = true;
    }
    private void Changed(object? sender, EventArgs e) => _window.Dispatcher.InvokeAsync(Update);
    private void Update()
    {
        if (_disposed) return;
        var state = _service.State;
        var account = state.SelectedAccount;
        double? remaining = account?.Snapshot?.Weekly?.RemainingPercent;
        string number = remaining is null ? "--" : ((int)Math.Floor(Math.Clamp(remaining.Value, 0, 100))).ToString();
        var color = remaining is null ? Color.FromArgb(150, 167, 188) : remaining > 20 ? Color.FromArgb(102, 224, 203) : remaining >= 10 ? Color.FromArgb(255, 196, 108) : Color.FromArgb(255, 129, 145);
        string key = number + color.ToArgb();
        if (_iconKey != key)
        {
            var old = _icon; _icon = CreateIcon(number, color); _tray.Icon = _icon; old?.Dispose(); _iconKey = key;
        }
        var text = account is null ? "Codex Tracker · aucun compte suivi" : $"{account.Profile.Email}\nSemaine : {(remaining is null ? "indisponible" : number + "% restant")}{(account.IsStale ? " · données anciennes" : "")}\nReset : {Display.Exact(account.Snapshot?.Weekly?.ResetsAt)}";
        _tray.Text = text.Length <= 127 ? text : text[..124] + "…";
        var menu = new Forms.ContextMenuStrip { BackColor = Color.FromArgb(19, 29, 43), ForeColor = Color.FromArgb(239, 245, 255), ShowImageMargin = false, Renderer = new DarkMenuRenderer() };
        menu.Items.Add("Ouvrir Codex Tracker", null, (_, _) => _window.ShowPanel());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var item in state.Accounts)
        {
            var submenu = new Forms.ToolStripMenuItem(item.Profile.Email) { Checked = item.Profile.Id == state.SelectedAccountId };
            submenu.DropDownItems.Add("Afficher dans l’icône", null, async (_, _) => await SafeAsync(() => _service.SelectAccountAsync(item.Profile.Id)));
            var use = submenu.DropDownItems.Add("Utiliser dans Codex…", null, (_, _) => { _window.ShowPanel(); _window.RequestSwitch(item.Profile.Id); });
            use.Enabled = state.CanSwitch && item.IsConnected && !item.IsActiveInCodex && !state.IsBusy;
            menu.Items.Add(submenu);
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
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
    internal static Icon CreateIcon(string number, Color color)
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(16, 25, 36));
        // The digits are the tray's primary information: use almost the full
        // 64px canvas so they remain readable when Explorer reduces it to 16px.
        graphics.FillEllipse(background, 0, 0, 64, 64);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = StringAlignment.Center; format.LineAlignment = StringAlignment.Center;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        float fontSize = number.Length == 3 ? 36 : number.Length == 1 ? 50 : 46;
        while (fontSize > 20)
        {
            using var candidate = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            if (graphics.MeasureString(number, candidate, int.MaxValue, format).Width <= 60) break;
            fontSize--;
        }
        using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(color);
        graphics.DrawString(number, font, foreground, new RectangleF(0, -1, 64, 64), format);
        IntPtr handle = bitmap.GetHicon();
        try { using var unmanaged = Icon.FromHandle(handle); return (Icon)unmanaged.Clone(); }
        finally { DestroyIcon(handle); }
    }
    public void Dispose()
    {
        _disposed = true; _service.Changed -= Changed; _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _icon?.Dispose();
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { }
    }
    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(42, 66, 78);
        public override Color MenuItemBorder => Color.FromArgb(74, 115, 114);
        public override Color ToolStripDropDownBackground => Color.FromArgb(19, 29, 43);
        public override Color MenuBorder => Color.FromArgb(51, 72, 94);
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    }
}
