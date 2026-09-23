using System;
using System.Runtime.InteropServices;
using CodexTracker.Core;
using Microsoft.Win32;

namespace CodexTracker.App;

// SHQueryUserNotificationState values are deliberately kept distinct from Focus Assist:
// the shell does not expose every modern notification preference through this API.
internal enum WindowsNotificationState { Unknown, Away, Busy, FullScreen, Presentation, Available, QuietTime, StoreApp }

internal sealed class WindowsNotificationDelivery(
    Action<string, string> show,
    Func<bool> available,
    Func<WindowsNotificationState>? state = null,
    Func<bool>? enabled = null)
{
    internal DeliveryResult Send(string title, string body, bool deferWhenBusy = false)
    {
        try
        {
            if (!available()) return new(DeliveryStatus.Failed, "L’icône de notification est indisponible. Rouvrez le tracker puis réessayez.");
            if (!(enabled ?? NotificationsEnabled)())
                return new(DeliveryStatus.Skipped, "Les notifications Windows sont désactivées. Vérifiez les paramètres de notification Windows puis réessayez.");
            var reason = (state ?? ReadState)() switch
            {
                WindowsNotificationState.Away => "Windows indique une session verrouillée ou inactive.",
                WindowsNotificationState.Busy => "Windows suspend les notifications : une application est en plein écran ou le mode présentation est actif.",
                WindowsNotificationState.FullScreen => "Windows suspend les notifications pendant une application en plein écran exclusif.",
                WindowsNotificationState.Presentation => "Le mode présentation de Windows suspend les notifications.",
                WindowsNotificationState.QuietTime => "Windows suspend temporairement les notifications après une ouverture de session ou une mise à niveau.",
                _ => null
            };
            if (reason is not null) return deferWhenBusy
                ? new(DeliveryStatus.Deferred, reason + " Le rappel sera réessayé tant qu’il reste pertinent.")
                : new(DeliveryStatus.Skipped, reason + " Quittez ce mode puis réessayez.");
            show(title, body);
            // A successful shell call is not evidence that a banner was displayed or read.
            return new(DeliveryStatus.Accepted, "Demande transmise à Windows ; affichage non confirmé. Si rien n’apparaît, vérifiez « Ne pas déranger » et les paramètres de notification Windows.");
        }
        catch (Exception)
        {
            return new(DeliveryStatus.Failed, "Windows n’a pas pu recevoir la notification. Rouvrez le tracker puis réessayez.");
        }
    }

    internal static WindowsNotificationState ReadState()
    {
        try { return SHQueryUserNotificationState(out var value) == 0 && Enum.IsDefined(value) ? value : WindowsNotificationState.Unknown; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return WindowsNotificationState.Unknown; }
    }

    private static bool NotificationsEnabled()
    {
        try
        {
            using var notifications = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            using var explorer = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var toastsEnabled = notifications?.GetValue("ToastEnabled") is not int toast || toast != 0;
            var balloonsEnabled = explorer?.GetValue("EnableBalloonTips") is not int balloon || balloon != 0;
            return toastsEnabled && balloonsEnabled;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        { return true; } // Unknown is not a confirmed disabled setting; let Windows decide.
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out WindowsNotificationState state);
}
