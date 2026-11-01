using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace CodexTracker.Widgets;

/// <summary>
/// COM server for the widget host: registered by the package manifest (windows.comServer + com.microsoft.windows.widgets)
/// and started with -RegisterProcessAsComServer. It lives while the host keeps widgets of this provider.
/// </summary>
internal static class Program
{
    internal static readonly ManualResetEvent Done = new(false);

    [MTAThread]
    private static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        if (!args.Contains("-RegisterProcessAsComServer")) return;
        var hr = CoRegisterClassObject(typeof(QuotaWidgetProvider).GUID, new ProviderFactory(), ClsctxLocalServer, RegclsMultipleUse, out var cookie);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try { Done.WaitOne(); }
        finally { CoRevokeClassObject(cookie); }
    }

    private const uint ClsctxLocalServer = 0x4, RegclsMultipleUse = 1;
    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
    [PreserveSig] int LockServer(bool lockServer);
}

[ComVisible(true)]
internal sealed class ProviderFactory : IClassFactory
{
    private const int NoAggregation = unchecked((int)0x80040110);
    private static readonly QuotaWidgetProvider Provider = new();

    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        if (outer != IntPtr.Zero) return NoAggregation;
        var unknown = MarshalInspectable<IWidgetProvider>.FromManaged(Provider);
        try { return Marshal.QueryInterface(unknown, in iid, out instance); }
        finally { Marshal.Release(unknown); }
    }

    public int LockServer(bool lockServer) => 0;
}

/// <summary>One definition ("quotas"): the active accounts of Codex and Claude Code, with the advice when running low.</summary>
[ComVisible(true), Guid("6E0B7C2A-9A4D-4F6B-9C2E-3D1A7B5E8F40")]
internal sealed class QuotaWidgetProvider : IWidgetProvider
{
    private readonly Dictionary<string, bool> _widgets = new();
    private readonly object _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private static readonly string Feed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker", "widget.json");
    private static readonly string Template = ReadTemplate();

    public QuotaWidgetProvider()
    {
        // Widgets created before this process started are still pinned on the board.
        foreach (var info in WidgetManager.GetDefault().GetWidgetInfos() ?? []) _widgets[info.WidgetContext.Id] = info.WidgetContext.IsActive;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Feed)!);
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(Feed)!, Path.GetFileName(Feed)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => UpdateAll(); _watcher.Created += (_, _) => UpdateAll(); _watcher.Renamed += (_, _) => UpdateAll();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { _watcher = null; }
        _timer = new Timer(_ => UpdateAll(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void CreateWidget(WidgetContext context) { lock (_gate) _widgets[context.Id] = true; Update(context.Id); }
    public void DeleteWidget(string widgetId, string customState)
    {
        lock (_gate) { _widgets.Remove(widgetId); if (_widgets.Count == 0) Program.Done.Set(); }
    }
    public void Activate(WidgetContext context) { lock (_gate) _widgets[context.Id] = true; Update(context.Id); }
    public void Deactivate(string widgetId) { lock (_gate) if (_widgets.ContainsKey(widgetId)) _widgets[widgetId] = false; }
    public void OnWidgetContextChanged(WidgetContextChangedArgs args) => Update(args.WidgetContext.Id);

    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        if (args.Verb != "open") return;
        // The tracker of this package: a second start brings the running instance forward.
        try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "..", "CodexTracker.exe")) { UseShellExecute = true }); }
        catch (Exception) { /* Nothing else to do from a widget. */ }
    }

    private void UpdateAll()
    {
        string[] active;
        lock (_gate) active = _widgets.Where(w => w.Value).Select(w => w.Key).ToArray();
        foreach (var id in active) Update(id);
    }

    private static void Update(string widgetId)
    {
        try { WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(widgetId) { Template = Template, Data = ReadFeed() }); }
        catch (Exception) { /* The host may have removed the widget meanwhile. */ }
    }

    private static string ReadFeed()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { if (File.Exists(Feed)) return Shape(File.ReadAllText(Feed)); break; }
            catch (IOException) { Thread.Sleep(100); }
            catch (System.Text.Json.JsonException) { break; }
        }
        return """{"title":"Codex Tracker","accounts":[],"empty":"Ouvrez Codex Tracker pour afficher vos quotas · Open Codex Tracker to show your quotas","advice":"","hasAdvice":false,"openLabel":"Codex Tracker","updated":""}""";
    }

    /// <summary>
    /// Card data from the tracker's summary: the most urgent account first (the small size shows only it), and a gauge
    /// drawn with two weighted columns whose colour follows the tracker's (accent, then warning and attention when low).
    /// </summary>
    internal static string Shape(string feed)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(feed)!.AsObject();
        var accounts = (root["accounts"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>().Select(a =>
        {
            var digits = new string((a["percent"]?.GetValue<string>() ?? "").TakeWhile(char.IsDigit).ToArray());
            int? value = int.TryParse(digits, out var parsed) ? Math.Clamp(parsed, 0, 100) : null;
            var color = a["color"]?.GetValue<string>() ?? "Default";
            return (Value: value, Node: new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = a["name"]?.GetValue<string>() ?? "",
                ["provider"] = a["provider"]?.GetValue<string>() ?? "",
                ["percent"] = a["percent"]?.GetValue<string>() ?? "--",
                ["color"] = color,
                ["reset"] = a["detail"]?.GetValue<string>() ?? "",
                ["fill"] = Math.Max(1, value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["rest"] = Math.Max(1, 100 - (value ?? 0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["bar"] = value is null ? "emphasis" : color switch { "Attention" => "attention", "Warning" => "warning", _ => "accent" },
            });
        }).OrderBy(a => a.Value ?? 101).Select(a => (System.Text.Json.Nodes.JsonNode)a.Node).ToArray();
        root["accounts"] = new System.Text.Json.Nodes.JsonArray(accounts);
        root["first"] = accounts.FirstOrDefault()?.DeepClone() ?? new System.Text.Json.Nodes.JsonObject();
        return root.ToJsonString();
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(QuotaWidgetProvider).Assembly.GetManifestResourceStream("QuotaCard.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
