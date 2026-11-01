using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
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
        // Diagnostic: the card data shaped from a summary file, without any widget host.
        if (args is ["--shape", var file]) { Console.Out.Write(QuotaWidgetProvider.Shape(File.ReadAllText(file))); return; }
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
        // The running tracker (classic or Store edition) owns the data: ask it, through its registered window messages.
        var tracker = FindWindow(null, "Codex Tracker");
        if (args.Verb == "refresh" && tracker != IntPtr.Zero) { PostMessage(tracker, RegisterWindowMessage("CodexTracker.RequestRefresh.v1"), IntPtr.Zero, IntPtr.Zero); return; }
        if (tracker != IntPtr.Zero) { PostMessage(tracker, RegisterWindowMessage("CodexTracker.RequestShow.v1"), IntPtr.Zero, IntPtr.Zero); return; }
        // Not running: start the tracker of this package, which opens in the notification area.
        try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "..", "CodexTracker.exe")) { UseShellExecute = true }); }
        catch (Exception) { /* Nothing else to do from a widget. */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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
    /// Card data from the tracker's summary. Small: ring of the most urgent account in use (PNG drawn here, the card
    /// format has no ring). Medium: every tracked account as a thin gauge, two weighted columns. Large: the week.
    /// A summary from an older tracker (only "accounts") still renders.
    /// </summary>
    internal static string Shape(string feed)
    {
        var root = JsonNode.Parse(feed)!.AsObject();
        var active = Items(root["active"] ?? root["accounts"]);
        foreach (var item in active)
        {
            item["ring"] = Ring.DataUri(Value(item), Color(item));
            item["reset"] ??= item["detail"]?.GetValue<string>() ?? "";
        }
        var tracked = Items(root["tracked"] ?? root["accounts"]);
        foreach (var item in tracked)
        {
            var value = Value(item);
            item["fill"] = Math.Max(1, value ?? 0).ToString(CultureInfo.InvariantCulture);
            item["rest"] = Math.Max(1, 100 - (value ?? 0)).ToString(CultureInfo.InvariantCulture);
            item["bar"] = Bar(item);
            item["gauge"] = Ring.Bar(value, Color(item));
            item["active"] ??= false;
        }
        var week = (root["week"] as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();
        foreach (var day in week) day["cell"] = Ring.Cell(day["level"] is JsonValue level && level.TryGetValue<int>(out var l) ? l : 0);
        root["week"] = new JsonArray([.. week.Select(d => (JsonNode)d)]);
        root["active"] = new JsonArray([.. active.OrderBy(i => Value(i) ?? 101).Select(i => (JsonNode)i)]);
        root["tracked"] = new JsonArray([.. tracked.OrderBy(i => Value(i) ?? 101).Select(i => (JsonNode)i)]);
        root["first"] = root["active"]!.AsArray().FirstOrDefault()?.DeepClone() ?? new JsonObject();
        foreach (var name in new[] { "reserve", "reserves", "advice", "empty" }) root[name] ??= "";
        root["hasAdvice"] ??= false;
        root["refreshLabel"] ??= "↻";
        return root.ToJsonString();
    }

    private static List<JsonObject> Items(JsonNode? array) =>
        (array as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();

    private static int? Value(JsonObject item)
    {
        if (item["value"] is JsonValue number && number.TryGetValue<double>(out var exact)) return (int)Math.Clamp(Math.Floor(exact), 0, 100);
        var digits = new string((item["percent"]?.GetValue<string>() ?? "").TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var parsed) ? Math.Clamp(parsed, 0, 100) : null;
    }

    private static System.Drawing.Color Color(JsonObject item) => Bar(item) switch { "attention" => Ring.Attention, "warning" => Ring.Warning, _ => Ring.Accent };

    private static string Bar(JsonObject item) => Value(item) is null ? "emphasis"
        : (item["color"]?.GetValue<string>() ?? "Default") switch { "Attention" => "attention", "Warning" => "warning", _ => "accent" };

    private static string ReadTemplate()
    {
        using var stream = typeof(QuotaWidgetProvider).Assembly.GetManifestResourceStream("QuotaCard.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
