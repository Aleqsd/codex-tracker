using System.Windows.Input;

namespace CodexTracker.App;

internal sealed class TrackerDialog : Window
{
    private readonly TextBlock _message;
    public TextBox? Input { get; }
    public StackPanel Extra { get; } = new();
    public TrackerDialog(Window owner, string title, string message, string? acceptText, string? cancelText, bool withInput = false)
    {
        Owner = owner; Title = title; Width = 560; MaxHeight = 740; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Foreground = Display.Brush("#EFF5FF");
        var border = new Border { Background = Display.Brush("#131E2D"), BorderBrush = Display.Brush("#3B546A"), BorderThickness = new Thickness(1), Padding = new Thickness(28) };
        var content = new StackPanel(); border.Child = content; Content = border;
        var heading = new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 17) };
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        content.Children.Add(heading);
        _message = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 21, Foreground = Display.Brush("#B8C9DC") };
        content.Children.Add(new ScrollViewer { Content = _message, MaxHeight = 385, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (withInput) { Input = new TextBox { Margin = new Thickness(0, 18, 0, 0) }; content.Children.Add(Input); Loaded += (_, _) => Input.Focus(); }
        content.Children.Add(Extra);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 25, 0, 0) };
        if (cancelText is not null) { var cancel = new Button { Content = cancelText, IsCancel = true, Margin = new Thickness(0, 0, 9, 0) }; cancel.Click += (_, _) => { DialogResult = false; }; buttons.Children.Add(cancel); }
        if (acceptText is not null) { var accept = new Button { Content = acceptText, IsDefault = true, Style = (Style)FindResource("PrimaryButton") }; accept.Click += (_, _) => { DialogResult = true; }; buttons.Children.Add(accept); }
        content.Children.Add(buttons);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }
    public void SetMessage(string message) => _message.Text = message;
}
