namespace CodexTracker.Codex;

public enum CodexHomeSource { Default, Environment, Explicit }
public enum CodexSessionStatus { NotChecked, Missing, Unreadable, Unsupported, Expired, Detected, InvalidHome }
public enum CodexServiceStatus { NotChecked, Missing, Compatible, Incompatible, Unavailable }
public enum CodexFailureCode
{
    None, Unknown, InvalidHome, SessionMissing, SessionUnreadable, UnsupportedSession, SessionExpired,
    CodexNotFound, ProtocolUnsupported, ServiceUnavailable, RequestTimedOut, AccountChanged
}

// Deliberately contains no paths, identity, credentials, quotas, or exception text. Safe to copy for support.
public sealed record CodexCompatibilityDiagnostic(CodexHomeSource HomeSource, CodexSessionStatus Session,
    CodexServiceStatus Service, CodexFailureCode LastFailure, DateTimeOffset? CheckedAt,
    DateTimeOffset? LastSuccessfulReadAt);

internal sealed record CodexAuthLocation(string? Path, CodexHomeSource Source)
{
    internal static CodexAuthLocation Resolve(string? explicitPath, string? environmentHome, string userProfile)
    {
        var source = explicitPath is not null ? CodexHomeSource.Explicit
            : !string.IsNullOrWhiteSpace(environmentHome) ? CodexHomeSource.Environment : CodexHomeSource.Default;
        try
        {
            var path = source switch
            {
                CodexHomeSource.Explicit => explicitPath!,
                CodexHomeSource.Environment => System.IO.Path.Combine(environmentHome!, "auth.json"),
                _ => System.IO.Path.Combine(userProfile, ".codex", "auth.json")
            };
            return new(System.IO.Path.GetFullPath(path), source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Never fall back to a different profile when a custom home is invalid.
            return new(null, source);
        }
    }
}
