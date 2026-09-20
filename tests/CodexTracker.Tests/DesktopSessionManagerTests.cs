using System.Text;
using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class DesktopSessionManagerTests
{
    [Fact]
    public async Task UnconfirmedSwitchPerformsNoDesktopOperation()
    {
        using var fixture = new SwitchFixture();
        var result = await fixture.Manager.ActivateAsync(fixture.Target, "demo@example.test", false);
        Assert.False(result.Success);
        Assert.False(result.NeedsUserVerification);
        Assert.Equal(0, fixture.Runtime.FindCalls);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.Null(fixture.Manager.PendingTargetEmail);
    }

    [Fact]
    public async Task UnsupportedVersionCannotCloseCodexOrReplaceCredentials()
    {
        using var fixture = new SwitchFixture();
        fixture.Runtime.Version = "99.0.0.0";
        Assert.False((await fixture.Manager.GetAvailabilityAsync()).CanSwitch);
        var result = await fixture.ActivateAsync();
        Assert.False(result.Success);
        Assert.Equal(0, fixture.Runtime.CloseCalls);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
    }

    [Fact]
    public async Task UnsupportedActiveAuthenticationDisablesSwitch()
    {
        using var fixture = new SwitchFixture();
        var apiKeyAuth = Encoding.UTF8.GetBytes("{\"OPENAI_API_KEY\":\"not-a-real-api-key\"}");
        await File.WriteAllBytesAsync(fixture.AuthPath, apiKeyAuth);
        Assert.False((await fixture.Manager.GetAvailabilityAsync()).CanSwitch);
        Assert.False((await fixture.ActivateAsync()).Success);
        Assert.Equal(0, fixture.Runtime.CloseCalls);
        Assert.Equal(apiKeyAuth, await File.ReadAllBytesAsync(fixture.AuthPath));
    }

    [Fact]
    public async Task TargetIdentityMismatchIsRejectedBeforeClose()
    {
        using var fixture = new SwitchFixture();
        var result = await fixture.Manager.ActivateAsync(fixture.Target, "wrong@example.test", true);
        Assert.False(result.Success);
        Assert.Equal(0, fixture.Runtime.CloseCalls);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
    }

    [Fact]
    public async Task RefusedCloseLeavesSessionAndJournalUntouched()
    {
        using var fixture = new SwitchFixture();
        fixture.Runtime.CloseSucceeds = false;
        var result = await fixture.ActivateAsync();
        Assert.False(result.Success);
        Assert.Equal(1, fixture.Runtime.CloseCalls);
        Assert.Equal(0, fixture.Runtime.LaunchCalls);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task FailedLaunchRestoresPreviousSessionAndRelaunchesIt()
    {
        using var fixture = new SwitchFixture();
        fixture.Runtime.LaunchResults.Enqueue(false);
        fixture.Runtime.LaunchResults.Enqueue(true);
        var result = await fixture.ActivateAsync();
        Assert.False(result.Success);
        Assert.False(result.NeedsUserVerification);
        Assert.Equal(2, fixture.Runtime.LaunchCalls);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.Equal(fixture.Previous, result.PreviousAuthJson);
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task FailedLaunchPreservesTargetRotationBeforeAutomaticRollback()
    {
        using var fixture = new SwitchFixture();
        var rotatedTarget = TestFixtures.Auth("demo@example.test", "rotated-during-failed-launch");
        fixture.Runtime.LaunchResults.Enqueue(false);
        fixture.Runtime.LaunchResults.Enqueue(true);
        fixture.Runtime.OnLaunch = call =>
        {
            if (call == 1) File.WriteAllBytes(fixture.AuthPath, rotatedTarget);
        };

        var result = await fixture.ActivateAsync();

        Assert.False(result.Success);
        Assert.False(result.NeedsUserVerification);
        Assert.Equal(rotatedTarget, result.DisplacedAuthJson);
        Assert.Equal(fixture.Previous, result.PreviousAuthJson);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.Equal(2, fixture.Runtime.LaunchCalls);
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task SuccessfulLaunchRemainsPendingUntilUserVerifiesAccount()
    {
        using var fixture = new SwitchFixture();
        var result = await fixture.ActivateAsync();
        Assert.False(result.Success);
        Assert.True(result.NeedsUserVerification);
        Assert.Equal(fixture.Target, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.Equal("demo@example.test", fixture.Manager.PendingTargetEmail);
        Assert.False((await fixture.Manager.GetAvailabilityAsync()).CanSwitch);
        Assert.DoesNotContain("before@example.test", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.JournalPath)));

        var verified = await fixture.Manager.ConfirmActivationAsync(true);
        Assert.True(verified.Success);
        Assert.False(verified.NeedsUserVerification);
        Assert.False(File.Exists(fixture.JournalPath));
        Assert.True((await fixture.Manager.GetAvailabilityAsync()).CanSwitch);
    }

    [Fact]
    public async Task VerificationRequiresRunningDesktopAndMatchingTarget()
    {
        using var fixture = new SwitchFixture();
        await fixture.ActivateAsync();
        fixture.Runtime.Running = false;
        Assert.False((await fixture.Manager.ConfirmActivationAsync(true)).Success);
        Assert.True(File.Exists(fixture.JournalPath));

        fixture.Runtime.Running = true;
        await File.WriteAllBytesAsync(fixture.AuthPath, fixture.Previous);
        var wrongAccount = await fixture.Manager.ConfirmActivationAsync(true);
        Assert.False(wrongAccount.Success);
        Assert.True(wrongAccount.NeedsUserVerification);
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task RejectedVerificationCanRestoreAfterTrackerRestart()
    {
        using var fixture = new SwitchFixture();
        await fixture.ActivateAsync();
        var resumed = new DesktopSessionManager(fixture.AuthPath, fixture.Directory.Root, fixture.Runtime);
        Assert.Equal("demo@example.test", resumed.PendingTargetEmail);
        var restored = await resumed.ConfirmActivationAsync(false);
        Assert.True(restored.Success);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.Equal(fixture.Target, restored.PreviousAuthJson);
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task BackupCapturesFinalTokenRotationWrittenDuringDesktopClose()
    {
        using var fixture = new SwitchFixture();
        var rotated = TestFixtures.Auth("before@example.test", "rotated-before-close");
        fixture.Runtime.OnClose = () => File.WriteAllBytes(fixture.AuthPath, rotated);
        var result = await fixture.ActivateAsync();
        Assert.Equal(rotated, result.PreviousAuthJson);
        fixture.Runtime.OnClose = null;
        await fixture.Manager.ConfirmActivationAsync(false);
        Assert.Equal(rotated, await File.ReadAllBytesAsync(fixture.AuthPath));
    }

    [Fact]
    public async Task CancellationAfterCloseRestartsPreviousSessionWithoutReplacingIt()
    {
        using var fixture = new SwitchFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Runtime.OnClose = cancellation.Cancel;
        var result = await fixture.Manager.ActivateAsync(fixture.Target, "demo@example.test", true, cancellation.Token);
        Assert.False(result.Success);
        Assert.Equal(1, fixture.Runtime.LaunchCalls);
        Assert.Equal(fixture.Previous, await File.ReadAllBytesAsync(fixture.AuthPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    private sealed class SwitchFixture : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public byte[] Previous { get; } = TestFixtures.Auth("before@example.test", "previous");
        public byte[] Target { get; } = TestFixtures.Auth("demo@example.test", "target");
        public FakeDesktopRuntime Runtime { get; } = new();
        public DesktopSessionManager Manager { get; }
        public string AuthPath => Directory.File("auth.json");
        public string JournalPath => Directory.File("pending-desktop-switch.bin");
        public SwitchFixture()
        {
            File.WriteAllBytes(AuthPath, Previous);
            Manager = new(AuthPath, Directory.Root, Runtime);
        }
        public Task<DesktopActivationResult> ActivateAsync() => Manager.ActivateAsync(Target, "demo@example.test", true);
        public void Dispose() => Directory.Dispose();
    }

    private sealed class FakeDesktopRuntime : IDesktopRuntime
    {
        public string Version { get; set; } = DesktopSessionManager.SupportedDesktopVersion;
        public bool Running { get; set; } = true;
        public bool CloseSucceeds { get; set; } = true;
        public int FindCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int LaunchCalls { get; private set; }
        public Queue<bool> LaunchResults { get; } = new();
        public Action? OnClose { get; set; }
        public Action<int>? OnLaunch { get; set; }
        public DesktopInstallation? FindInstallation()
        {
            FindCalls++;
            return new("C:\\Fixtures\\Codex.exe", Version);
        }
        public bool IsRunning(DesktopInstallation installation) => Running;
        public Task<bool> CloseAsync(DesktopInstallation installation, CancellationToken cancellationToken)
        {
            CloseCalls++;
            OnClose?.Invoke();
            if (CloseSucceeds) Running = false;
            return Task.FromResult(CloseSucceeds);
        }
        public Task<bool> LaunchAsync(DesktopInstallation installation, CancellationToken cancellationToken)
        {
            LaunchCalls++;
            Running = LaunchResults.Count == 0 || LaunchResults.Dequeue();
            OnLaunch?.Invoke(LaunchCalls);
            return Task.FromResult(Running);
        }
    }
}
