using System.Security.Cryptography;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed class CurrentAccountUsageReader(string authPath, ProfileStore store, TrackerServiceOptions options) : IAccountUsageReader
{
    public async Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
    {
        var identity = await ReadIdentityAsync(authPath, cancellationToken);
        Validate(identity, profile.Email, expectedAccountId);
        var executable = options.CodexExecutablePath ?? CodexLocator.FindExecutable()
            ?? throw new TrackerException("Ouvrez ou installez Codex pour lire les quotas.");
        var home = store.CreateRuntime();
        try
        {
            await using var client = await AppServerClient.StartAsync(executable, home, true, async token =>
            {
                var current = await ReadIdentityAsync(authPath, token);
                Validate(current, profile.Email, expectedAccountId);
                // Codex alone renews its login. The tracker can only re-read the latest access token.
                return new { accessToken = current.AccessToken, chatgptAccountId = current.AccountId, chatgptPlanType = current.PlanType };
            }, cancellationToken);
            await client.RequestAsync("account/login/start", new { type = "chatgptAuthTokens", accessToken = identity.AccessToken,
                chatgptAccountId = identity.AccountId, chatgptPlanType = identity.PlanType }, cancellationToken);
            var response = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
            if (!response.TryGetProperty("account", out var account) || AuthDocument.Read(account, "type") != "chatgpt" ||
                !string.Equals(AuthDocument.Read(account, "email"), profile.Email, StringComparison.OrdinalIgnoreCase))
                throw new TrackerException("Codex n'a pas confirmé le compte actif. Ouvrez ce compte dans Codex puis actualisez.");
            var limits = await client.RequestAsync("account/rateLimits/read", new { }, cancellationToken);
            if (AuthDocument.Read(limits, "accountId") is { } quotaAccountId && quotaAccountId != expectedAccountId)
                throw new TrackerException("Le relevé appartient à un autre compte. Il a été ignoré.");
            Validate(await ReadIdentityAsync(authPath, cancellationToken), profile.Email, expectedAccountId);
            var snapshot = RateLimitParser.Parse(limits, profile.Email, AuthDocument.Read(account, "planType") ?? identity.PlanType, DateTimeOffset.UtcNow);
            return snapshot with { PlanMultiplier = AuthDocument.PlanMultiplier(snapshot.PlanType),
                SubscriptionStartedAt = identity.SubscriptionStartedAt, SubscriptionEndsAt = identity.SubscriptionEndsAt };
        }
        finally
        {
            // A terminated child can briefly retain SQLite handles while Windows reaps it.
            // Do not lose a valid quota response because this non-credential cache is still locked.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try { store.DeleteRuntime(home); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 11) break; // Retried on next startup, under the same private ACL.
                    await Task.Delay(100);
                }
            }
        }
    }

    internal static async Task<AuthDocument> ReadIdentityAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > 1_000_000) throw new TrackerException("Le fichier de session Codex est incompatible.");
                using var buffer = new MemoryStream();
                try
                {
                    await file.CopyToAsync(buffer, cancellationToken);
                    var bytes = buffer.ToArray();
                    try { return AuthDocument.Parse(bytes); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                finally { CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
            }
            catch (Exception ex) when (attempt < 2 && ex is IOException or JsonException or TrackerException)
            { await Task.Delay(80, cancellationToken); }
        }
    }

    private static void Validate(AuthDocument identity, string email, string accountId)
    {
        if (!string.Equals(identity.Email, email, StringComparison.OrdinalIgnoreCase) || identity.AccountId != accountId)
            throw new TrackerException("Le compte a changé dans Codex. Le relevé précédent a été ignoré.");
    }
}
