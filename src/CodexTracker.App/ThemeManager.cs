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
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public static Brush GetBrush(string key) => (Brush)System.Windows.Application.Current.FindResource(key);
    public static Color GetColor(string key) => ((SolidColorBrush)GetBrush(key)).Color;
    public void Dispose()
    {
        _disposed = true; _preferences.Changed -= PreferencesChanged; SystemEvents.UserPreferenceChanged -= SystemChanged;
    }
    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["BackgroundBrush"] = "#181818", ["ChromeBrush"] = "#1C1C1C", ["PanelBrush"] = "#222222", ["LineBrush"] = "#393939",
        ["TextBrush"] = "#ECECE8", ["MutedBrush"] = "#A5A5A5", ["SubtleBrush"] = "#858585", ["AccentBrush"] = "#DEDEDB",
        ["ButtonBrush"] = "#292929", ["ButtonHoverBrush"] = "#343434", ["PrimaryButtonBrush"] = "#E8E8E4", ["PrimaryButtonHoverBrush"] = "#D2D2CE", ["PrimaryButtonTextBrush"] = "#222222",
        ["TrackBrush"] = "#3B3B3B", ["SelectedBrush"] = "#343434", ["FocusBrush"] = "#909090", ["ScrollBrush"] = "#515151", ["AvatarBrush"] = "#383838",
        ["GoodBrush"] = "#B0C6B6", ["WarningBrush"] = "#D4B47A", ["DangerBrush"] = "#D49B9B", ["ErrorBackgroundBrush"] = "#342B2B", ["ChartBrush"] = "#DDDDDA", ["ChartFillBrush"] = "#202020"
    };
    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["BackgroundBrush"] = "#FAFAF8", ["ChromeBrush"] = "#F3F3F0", ["PanelBrush"] = "#FFFFFF", ["LineBrush"] = "#DEDEDA",
        ["TextBrush"] = "#252524", ["MutedBrush"] = "#6A6A66", ["SubtleBrush"] = "#7C7C77", ["AccentBrush"] = "#434340",
        ["ButtonBrush"] = "#F2F2EE", ["ButtonHoverBrush"] = "#E6E6E0", ["PrimaryButtonBrush"] = "#292927", ["PrimaryButtonHoverBrush"] = "#454542", ["PrimaryButtonTextBrush"] = "#FAFAF7",
        ["TrackBrush"] = "#E1E1DB", ["SelectedBrush"] = "#EEEEEA", ["FocusBrush"] = "#757570", ["ScrollBrush"] = "#C9C9C2", ["AvatarBrush"] = "#E8E8E2",
        ["GoodBrush"] = "#4F6C57", ["WarningBrush"] = "#86652B", ["DangerBrush"] = "#A14F4F", ["ErrorBackgroundBrush"] = "#F6EDEB", ["ChartBrush"] = "#454540", ["ChartFillBrush"] = "#F6F6F2"
    };
}
