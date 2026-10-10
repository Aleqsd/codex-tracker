using Microsoft.Win32;

namespace CodexTracker.App;

/// <summary>
/// codextracker://account/{id} links, written into calendar events, open that account in the tracker.
/// The installer registers the scheme for the current user; a portable copy registers itself when it exports events.
/// </summary>
internal static class AccountLink
{
    private const string Scheme = "codextracker";
    private const string ClassKey = @"Software\Classes\" + Scheme;

    public static Guid? Parse(IEnumerable<string> args) => args.Select(Parse).FirstOrDefault(id => id is not null);
    public static Guid? Parse(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Scheme && uri.Host == "account" &&
        Guid.TryParse(uri.AbsolutePath.Trim('/'), out var id) ? id : null;

    // The account travels in the two pointer-sized parameters of the single-instance window message.
    public static (IntPtr First, IntPtr Second) Pack(Guid id)
    {
        var bytes = id.ToByteArray();
        return ((IntPtr)BitConverter.ToInt64(bytes, 0), (IntPtr)BitConverter.ToInt64(bytes, 8));
    }
    public static Guid Unpack(IntPtr first, IntPtr second)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(first.ToInt64()).CopyTo(bytes, 0);
        BitConverter.GetBytes(second.ToInt64()).CopyTo(bytes, 8);
        return new Guid(bytes);
    }

    public static void EnsureRegistered(string executable)
    {
        var command = $"\"{executable}\" \"%1\"";
        using (var existing = Registry.CurrentUser.OpenSubKey(ClassKey + @"\shell\open\command"))
            if (existing?.GetValue(null) as string == command) return;
        using var key = Registry.CurrentUser.CreateSubKey(ClassKey);
        key.SetValue(null, "URL:Codex Tracker");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{executable}\",0");
        using var open = key.CreateSubKey(@"shell\open\command");
        open.SetValue(null, command);
    }
}
