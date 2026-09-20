using System.Windows.Input;

namespace CodexTracker.App;

internal sealed class TrackerDialog : Window
{
    private readonly TextBlock _message;
    private readonly PreferencesStore? _preferences;
    private string _rawMessage;
    public TextBox? Input { get; }
    public StackPanel Extra { get; } = new();
    public TrackerDialog(Window owner, string title, string message, string? acceptText, string? cancelText, bool withInput = false)
    {
        _rawMessage = message;
        for (Window? ancestor = owner; ancestor is not null; ancestor = ancestor.Owner)
            if (ancestor is MainWindow main) { _preferences = main.Preferences; break; }
        Owner = owner; Title = title; Width = 560; MaxHeight = 740; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "TextBrush");
        var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(25) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var content = new StackPanel(); border.Child = content; Content = border;
        var heading = new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 17) };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        content.Children.Add(heading);
        _message = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21 };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(new ScrollViewer { Content = _message, MaxHeight = 385, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (withInput) { Input = new TextBox { Margin = new Thickness(0, 18, 0, 0) }; content.Children.Add(Input); Loaded += (_, _) => Input.Focus(); }
        content.Children.Add(Extra);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 25, 0, 0) };
        if (cancelText is not null) { var cancel = new Button { Content = cancelText, IsCancel = true, Margin = new Thickness(0, 0, 9, 0) }; cancel.Click += (_, _) => { DialogResult = false; }; buttons.Children.Add(cancel); }
        if (acceptText is not null) { var accept = new Button { Content = acceptText, IsDefault = true, Style = (Style)FindResource("PrimaryButton") }; accept.Click += (_, _) => { DialogResult = true; }; buttons.Children.Add(accept); }
        content.Children.Add(buttons);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
        if (_preferences is not null) { _preferences.Changed += PreferencesChanged; Closed += (_, _) => _preferences.Changed -= PreferencesChanged; }
        UpdatePrivacy();
    }
    private void PreferencesChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdatePrivacy);
    private void UpdatePrivacy() => _message.Text = Display.SafeText(_rawMessage, _preferences?.Current.PrivacyMode == true);
    public void SetMessage(string message) { _rawMessage = message; UpdatePrivacy(); }
}
