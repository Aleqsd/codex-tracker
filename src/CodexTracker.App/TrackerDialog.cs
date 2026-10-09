using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace CodexTracker.App;

internal sealed class TrackerDialog : Window
{
    private readonly TextBlock _message;
    private readonly PreferencesStore? _preferences;
    private readonly ThemeManager? _theme;
    private string _rawMessage;
    public TextBox? Input { get; }
    public StackPanel Extra { get; } = new();
    public TrackerDialog(Window owner, string title, string message, string? acceptText, string? cancelText, bool withInput = false)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        _rawMessage = message;
        for (Window? ancestor = owner; ancestor is not null; ancestor = ancestor.Owner)
            if (ancestor is MainWindow main) { _preferences = main.Preferences; _theme = main.Theme; break; }
        Owner = owner; Title = title; Width = 560; MaxHeight = 740; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "TextBrush");
        var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(28, 26, 28, 24) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var content = new StackPanel(); border.Child = content; Content = border;
        UiMotion.SetRise(content, 10); UiMotion.SetFadeOnShow(content, true);
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        content.Children.Add(heading);
        _message = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(new ScrollViewer { Content = _message, MaxHeight = 385, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (withInput) { Input = new TextBox { Margin = new Thickness(0, 18, 0, 0) }; content.Children.Add(Input); Loaded += (_, _) => Input.Focus(); }
        content.Children.Add(Extra);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        if (cancelText is not null) { var cancel = new Button { Content = cancelText, IsCancel = true, Margin = new Thickness(0, 0, 9, 0) }; cancel.Click += (_, _) => { DialogResult = false; }; buttons.Children.Add(cancel); }
        if (acceptText is not null) { var accept = new Button { Content = acceptText, IsDefault = true, Style = (Style)FindResource("PrimaryButton") }; accept.Click += (_, _) => { DialogResult = true; }; buttons.Children.Add(accept); }
        content.Children.Add(buttons);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
        if (_preferences is not null) { _preferences.Changed += PreferencesChanged; Closed += (_, _) => _preferences.Changed -= PreferencesChanged; }
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            int dark = _theme?.IsDark == true ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, 4);
            int rounded = 2; DwmSetWindowAttribute(handle, 33, ref rounded, 4);
        };
        UpdatePrivacy();
    }
    private void PreferencesChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdatePrivacy);
    private void UpdatePrivacy() => _message.Text = Display.SafeText(_rawMessage, _preferences?.Current.PrivacyMode == true);
    public void SetMessage(string message) { _rawMessage = message; UpdatePrivacy(); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
