using Microsoft.Win32;

namespace CodexTracker.App;

internal sealed class ThemeManager : IDisposable
{
    private readonly PreferencesStore _preferences;
    public event EventHandler? Changed;
    public bool IsDark { get; private set; }
    private bool _disposed;
    public ThemeManager(PreferencesStore preferences)
    {
        _preferences = preferences;
        preferences.Changed += PreferencesChanged;
        SystemEvents.UserPreferenceChanged += SystemChanged;
        Apply();
    }
    private void PreferencesChanged(object? sender, EventArgs e) => System.Windows.Application.Current.Dispatcher.InvokeAsync(Apply);
    private void SystemChanged(object sender, UserPreferenceChangedEventArgs e) => System.Windows.Application.Current.Dispatcher.InvokeAsync(Apply);
    private static bool WindowsIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
    }
    private void Apply()
    {
        if (_disposed) return;
        IsDark = _preferences.Current.ThemeMode switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => WindowsIsDark() };
        foreach (var (key, color) in IsDark ? DarkPalette : LightPalette)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
            brush.Freeze(); System.Windows.Application.Current.Resources[key] = brush;
        }
        System.Windows.Application.Current.Resources["CodexProviderIcon"] = ProviderIcon(IsDark ? "codex-dark.png" : "codex-light.png");
        System.Windows.Application.Current.Resources["ClaudeProviderIcon"] = ProviderIcon("claude.png");
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private static System.Windows.Media.Imaging.BitmapImage ProviderIcon(string name)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/CodexTracker;component/Assets/Providers/" + name));
        image.Freeze(); return image;
    }
    public static Brush GetBrush(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
    public static Color GetColor(string key) => ((SolidColorBrush)GetBrush(key)).Color;
    public void Dispose()
    {
        _disposed = true; _preferences.Changed -= PreferencesChanged; SystemEvents.UserPreferenceChanged -= SystemChanged;
    }
    // One indigo accent for quotas, selection and primary actions; green, amber and red stay reserved for states.
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["BackgroundBrush"] = "#101114", ["ChromeBrush"] = "#0D0E10", ["PanelBrush"] = "#17181C", ["RaisedBrush"] = "#1F2126", ["LineBrush"] = "#272930",
        ["TextBrush"] = "#ECEDF0", ["MutedBrush"] = "#989BA4", ["SubtleBrush"] = "#6F727B", ["AccentBrush"] = "#8E96FF", ["AccentSoftBrush"] = "#1F2244",
        ["ButtonBrush"] = "#1C1E23", ["ButtonHoverBrush"] = "#262830", ["SegmentBrush"] = "#2D3038",
        ["PrimaryButtonBrush"] = "#5C64F0", ["PrimaryButtonHoverBrush"] = "#6D75F5", ["PrimaryButtonTextBrush"] = "#FFFFFF",
        ["TrackBrush"] = "#2A2C33", ["SelectedBrush"] = "#24262C", ["FocusBrush"] = "#8E96FF", ["ScrollBrush"] = "#383B43", ["AvatarBrush"] = "#2A2C33",
        ["GoodBrush"] = "#5FCF9C", ["GoodSoftBrush"] = "#13261D", ["WarningBrush"] = "#E8B65F", ["WarningSoftBrush"] = "#2A2214", ["DangerBrush"] = "#F27B7B",
        ["ErrorBackgroundBrush"] = "#2B191B", ["ChartBrush"] = "#8E96FF", ["ChartFillBrush"] = "#1C1F3A"
    };
    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["BackgroundBrush"] = "#F4F5F7", ["ChromeBrush"] = "#ECEEF1", ["PanelBrush"] = "#FFFFFF", ["RaisedBrush"] = "#ECEEF2", ["LineBrush"] = "#E1E3E8",
        ["TextBrush"] = "#16171A", ["MutedBrush"] = "#5D616A", ["SubtleBrush"] = "#868A93", ["AccentBrush"] = "#4F57E3", ["AccentSoftBrush"] = "#ECEDFC",
        ["ButtonBrush"] = "#FFFFFF", ["ButtonHoverBrush"] = "#EFF0F4", ["SegmentBrush"] = "#FFFFFF",
        ["PrimaryButtonBrush"] = "#4F57E3", ["PrimaryButtonHoverBrush"] = "#434BD3", ["PrimaryButtonTextBrush"] = "#FFFFFF",
        ["TrackBrush"] = "#E4E6EB", ["SelectedBrush"] = "#EEF0F4", ["FocusBrush"] = "#4F57E3", ["ScrollBrush"] = "#C6C9D0", ["AvatarBrush"] = "#E4E6EB",
        ["GoodBrush"] = "#1C8456", ["GoodSoftBrush"] = "#E5F4EC", ["WarningBrush"] = "#A4660C", ["WarningSoftBrush"] = "#FBF0DF", ["DangerBrush"] = "#C94545",
        ["ErrorBackgroundBrush"] = "#FBEAEA", ["ChartBrush"] = "#4F57E3", ["ChartFillBrush"] = "#E9EBFC"
    };
}
