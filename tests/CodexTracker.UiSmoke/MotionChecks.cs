using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CodexTracker.App;

internal static class MotionChecks
{
    private static int _checks;
    private static void Check(bool result, string message)
    { if (!result) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static async Task Settle()
    {
        await Task.Delay(260);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static IEnumerable<Transform> Transforms(Transform transform)
    {
        yield return transform;
        if (transform is TransformGroup group)
            foreach (var child in group.Children.SelectMany(Transforms)) yield return child;
    }

    internal static async Task Run(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("MainTabs");
        var accounts = (TabItem)window.FindName("AccountsTab");
        var host = (FrameworkElement)tabs.Template.FindName("PART_SelectedContentHost", tabs);
        var draft = new TextBox { Text = "Brouillon fictif" };
        var originalTransform = new TranslateTransform(2, 0);
        var button = new Button { Content = "Action fictive", RenderTransform = originalTransform };
        var content = new StackPanel(); content.Children.Add(draft); content.Children.Add(button);
        var opacitySource = new Slider { Value = 0.8 };
        content.SetBinding(UIElement.OpacityProperty, new Binding("Value") { Source = opacitySource });
        UiMotion.SetFadeOnShow(content, true);
        var fixture = new Window
        {
            Owner = window, Title = "Animations · démo", Width = 320, Height = 160,
            ShowInTaskbar = false, Content = content, Style = (Style)window.FindResource(typeof(Window))
        };
        try
        {
            for (var i = 0; i < 8; i++) tabs.SelectedIndex = i % 2;
            tabs.SelectedItem = accounts;
            accounts.Focus();
            await Settle();
            Check(host.Opacity == 1 && !host.HasAnimatedProperties && accounts.IsKeyboardFocused,
                "Rapid tab navigation settles completely without stealing keyboard focus");
            UiMotion.SetEnabled(window, false);
            tabs.SelectedIndex = 1;
            Check(host.Opacity == 1 && !host.HasAnimatedProperties,
                "Reduced motion inherited from the window makes navigation immediate");
            UiMotion.SetEnabled(window, true); tabs.SelectedItem = accounts;

            fixture.Show(); await Settle(); draft.Focus();
            UiMotion.FadeIn(content);
            if (UiMotion.Allowed(content))
                Check(content.HasAnimatedProperties, "Visible content animates when Windows allows motion");
            else
                Check(!content.HasAnimatedProperties, "Windows reduced motion or high contrast suppresses transitions");
            for (var i = 0; i < 5; i++) UiMotion.FadeIn(content);
            await Settle();
            opacitySource.Value = 0.7;
            Check(content.Opacity == 0.7 && BindingOperations.IsDataBound(content, UIElement.OpacityProperty)
                && !content.HasAnimatedProperties && draft.IsKeyboardFocused && draft.Text == "Brouillon fictif",
                "Repeated fades release their clocks and preserve bindings, drafts and keyboard focus");

            UiMotion.SetScale(button, 0.975); UiMotion.SetScale(button, 1);
            UiMotion.SetAngle(button, 180); UiMotion.SetAngle(button, 0); UiMotion.SetAngle(button, 90);
            await Settle();
            var transforms = Transforms(button.RenderTransform).ToArray();
            Check(transforms.Contains(originalTransform) && transforms.OfType<ScaleTransform>().Single().ScaleX == 1
                && transforms.OfType<RotateTransform>().Single().Angle == 90
                && transforms.All(t => !t.HasAnimatedProperties),
                "Rapid press and chevron reversal preserve existing transforms and release completed clocks");

            UiMotion.FadeIn(content); UiMotion.SetAngle(button, 180); UiMotion.SetScale(button, 0.975);
            UiMotion.SetEnabled(content, false);
            Check(content.Opacity == 0.7 && !content.HasAnimatedProperties
                && transforms.OfType<ScaleTransform>().Single().ScaleX == 1
                && transforms.OfType<RotateTransform>().Single().Angle == 180
                && transforms.All(t => !t.HasAnimatedProperties),
                "Disabling motion mid-transition immediately restores readable content and final orientation");
            UiMotion.SetAngle(button, 0); UiMotion.SetScale(button, 1); UiMotion.SetScale(button, 0.975);
            Check(transforms.OfType<RotateTransform>().Single().Angle == 0
                && transforms.OfType<ScaleTransform>().Single().ScaleX == 1,
                "Reduced motion keeps disclosure state correct without a button compression");

            UiMotion.SetEnabled(content, true); UiMotion.FadeIn(content); fixture.Hide();
            Check(!content.HasAnimatedProperties && transforms.All(t => !t.HasAnimatedProperties),
                "Hidden UI stops all active motion");
            fixture.Show(); await Settle();
            Check(content.Opacity == 0.7 && !content.HasAnimatedProperties,
                "Reopening the same UI never leaves it faded or invisible");
            UiMotion.Suppressed = true; UiMotion.FadeIn(content); UiMotion.SetAngle(button, 90);
            Check(content.Opacity == 0.7 && !content.HasAnimatedProperties
                && transforms.OfType<RotateTransform>().Single().Angle == 90,
                "Static preview mode captures final content and disclosure state immediately");
        }
        finally
        {
            UiMotion.Suppressed = false; UiMotion.SetEnabled(window, true);
            fixture.Close(); tabs.SelectedItem = accounts; window.Activate();
        }
        Console.WriteLine($"PASS {_checks} motion and accessibility checks");
    }
}
