namespace CodexTracker.Codex;

public sealed record DesktopAvailability(bool CanSwitch, bool IsRunning, string? Reason, string? Version);
public sealed record DesktopActivationResult(bool Success, string Message, bool NeedsUserVerification = false,
    byte[]? PreviousAuthJson = null, byte[]? DisplacedAuthJson = null);

public interface IDesktopSessionManager
{
    string AuthFilePath { get; }
    string? PendingTargetEmail { get; }
    Task<DesktopAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<DesktopActivationResult> ActivateAsync(byte[] targetAuthJson, string expectedEmail,
        bool confirmed, CancellationToken cancellationToken = default);
    Task<DesktopActivationResult> ConfirmActivationAsync(bool accepted, CancellationToken cancellationToken = default);
}
