using System.Text;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ProfileStoreTests
{
    [Fact]
    public void VaultRoundTripUsesCiphertextAndSeparatesAccountEntropy()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var plain = TestFixtures.Auth();
        store.SaveAuth(id, plain);
        Assert.True(store.HasAuth(id));
        Assert.Equal(plain, store.LoadAuth(id));
        var vaultFile = directory.File(Path.Combine("vault", $"{id:D}.bin"));
        var cipher = File.ReadAllBytes(vaultFile);
        Assert.False(cipher.AsSpan().SequenceEqual(plain));
        Assert.DoesNotContain("not-a-real-refresh-token", Encoding.UTF8.GetString(cipher));
        File.Copy(vaultFile, directory.File(Path.Combine("vault", $"{otherId:D}.bin")));
        Assert.Throws<TrackerException>(() => store.LoadAuth(otherId));
        store.DeleteAuth(id);
        Assert.False(store.HasAuth(id));
    }

    [Fact]
    public void ExclusiveStoreLeaseRejectsAnotherInstance()
    {
        using var directory = new TestDirectory();
        using var first = new ProfileStore(directory.Root);
        first.Open();
        using var second = new ProfileStore(directory.Root);
        Assert.Throws<TrackerException>(second.Open);
    }

    [Fact]
    public void RuntimeCleanupCannotDeleteDataDirectory()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var runtime = store.CreateRuntime(Guid.NewGuid(), TestFixtures.Auth());
        Assert.True(File.Exists(Path.Combine(runtime, "auth.json")));
        Assert.Throws<InvalidOperationException>(() => store.DeleteRuntime(directory.Root));
        Assert.Throws<InvalidOperationException>(() => store.DeleteRuntime(directory.File("runtime")));
        store.DeleteRuntime(runtime);
        Assert.False(Directory.Exists(runtime));
        Assert.True(Directory.Exists(directory.Root));
    }

    [Fact]
    public void RecoveryPreservesRotatedSessionOnlyForMatchingKnownProfile()
    {
        using var directory = new TestDirectory();
        var profile = new AccountProfile(Guid.NewGuid(), "demo@example.test");
        var rotated = TestFixtures.Auth(marker: "rotated");
        string runtime;
        using (var first = new ProfileStore(directory.Root))
        {
            first.Open();
            first.SaveSettings(new([profile], profile.Id, true));
            first.SaveAuth(profile.Id, TestFixtures.Auth(marker: "before"));
            runtime = first.CreateRuntime(profile.Id, rotated);
        }
        using var resumed = new ProfileStore(directory.Root);
        resumed.Open();
        Assert.Equal(rotated, resumed.LoadAuth(profile.Id));
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void RecoveryRejectsWrongIdentityAndUnverifiedLoginRuntime()
    {
        using var directory = new TestDirectory();
        var profile = new AccountProfile(Guid.NewGuid(), "demo@example.test");
        var unverified = Guid.NewGuid();
        var saved = TestFixtures.Auth(marker: "saved");
        using (var first = new ProfileStore(directory.Root))
        {
            first.Open();
            first.SaveSettings(new([profile], profile.Id, true));
            first.SaveAuth(profile.Id, saved);
            first.CreateRuntime(profile.Id, TestFixtures.Auth("wrong@example.test", "wrong"));
            first.CreateRuntime(unverified, TestFixtures.Auth());
        }
        using var resumed = new ProfileStore(directory.Root);
        resumed.Open();
        Assert.Equal(saved, resumed.LoadAuth(profile.Id));
        Assert.False(resumed.HasAuth(unverified));
        Assert.Empty(Directory.EnumerateDirectories(directory.File("runtime")));
    }
}
