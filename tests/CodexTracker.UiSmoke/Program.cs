using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Core;

// Exercise real WPF windows and the production message handler with fictional data only.
internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly Assembly = typeof(MainWindow).Assembly;
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        var app = new TestApplication();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var sourceXaml = System.Xml.Linq.XDocument.Load("src/CodexTracker.App/App.xaml");
        var resources = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            sourceXaml.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        app.Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? window = null;
            ITrackerService? service = null;
            HwndSource? source = null;
            HwndSourceHook? hook = null;
            try
            {
                service = (ITrackerService)Activator.CreateInstance(Assembly.GetType("CodexTracker.App.DemoTrackerService")!, true)!;
                var preferences = Activator.CreateInstance(Assembly.GetType("CodexTracker.App.PreferencesStore")!, Instance, null, [false, null], null)!;
                window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Instance, null, [service, true, preferences, new UpdateService()], null)!;
                app.MainWindow = window;
                var handle = new WindowInteropHelper(window).EnsureHandle();
                source = HwndSource.FromHwnd(handle)!;
                hook = (HwndSourceHook)typeof(App).GetMethod("HandleWindowMessage", Instance)!.CreateDelegate(typeof(HwndSourceHook), app);
                source.AddHook(hook);
                await window.InitializeAsync();
                await Settle();
                Check(!window.IsVisible && VisualTreeHelper.GetChildrenCount(window) == 0, "Background startup has no attached visual tree yet");

                // Reproduce the old bug: a visible HWND with no WPF content.
                ShowWindow(handle, 9);
                await Settle();

                Check(IsWindowVisible(handle) && VisualTreeHelper.GetChildrenCount(window) == 0,
                    "Legacy native activation does not create WPF content");

                await RequestShow(handle);
                CheckRendered(window, "First activation from background renders WPF content");
                window.Hide();
                await RequestShow(handle);
                CheckRendered(window, "Reopening a hidden window renders content");
                window.WindowState = WindowState.Minimized;
                await RequestShow(handle);
                CheckRendered(window, "Activation restores a minimized window");
                await RequestShow(handle);
                CheckRendered(window, "Repeated activation retains the same rendered window");
                await FeatureChecks.Run(window, service);
                await AssistantChecks.Run(window);
                window.Hide();
                typeof(App).GetField("_exiting", Instance)!.SetValue(app, true);
                await RequestShow(handle);
                Check(!window.IsVisible, "Activation during shutdown does not reopen the window");
                Console.WriteLine($"PASS {_checks} WPF activation checks");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                if (source is not null && hook is not null) source.RemoveHook(hook);
                window?.PrepareExit(); window?.Close();
                if (service is not null) await service.DisposeAsync();
                app.Dispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        return Environment.ExitCode;
    }

    private sealed class TestApplication : App { protected override void OnStartup(StartupEventArgs e) { } }

    private static async Task RequestShow(IntPtr handle)
    {
        if (!PostMessage(handle, RegisterWindowMessage("CodexTracker.RequestShow.v1"), IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Could not send activation message");
        await Settle();
    }
    private static async Task Settle()
    {
        await Task.Delay(150);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void CheckRendered(MainWindow window, string label) => Check(
        window.IsVisible && window.WindowState == WindowState.Normal && window.ActualWidth > 0 &&
        Descendants(window).OfType<TextBlock>().Any(t => t.IsVisible && t.ActualWidth > 0 && t.Text.Contains("alex@example.com")), label);
    private static void Check(bool result, string label)
    {
        if (!result) throw new InvalidOperationException(label);
        _checks++; Console.WriteLine("PASS " + label);
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
