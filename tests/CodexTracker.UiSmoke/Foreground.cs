using System.Windows;
using System.Windows.Input;

/// <summary>Windows may keep the foreground on the user's own application while the suite runs.</summary>
// It refuses activation to a background process after recent user input: focus checks then use
// logical focus, and only an interaction that truly needs the foreground is reported as skipped.
internal static class Foreground
{
    internal static int Skipped { get; private set; }

    /// <summary>Activates the window and waits up to one second for Windows to accept it.</summary>
    internal static async Task<bool> TryActivate(Window window)
    {
        for (var attempt = 0; attempt < 10 && !window.IsActive; attempt++) { window.Activate(); await Task.Delay(100); }
        return window.IsActive;
    }

    /// <summary>Keyboard focus in an active window; logical focus in its focus scope otherwise.</summary>
    internal static bool HasFocus(UIElement element) => element.IsKeyboardFocused ||
        Window.GetWindow(element) is { IsActive: false } && FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)) == element;

    internal static void Skip(string check)
    {
        Skipped++;
        Console.WriteLine($"SKIP {check} (Windows a gardé le premier plan sur une autre application)");
    }
}
