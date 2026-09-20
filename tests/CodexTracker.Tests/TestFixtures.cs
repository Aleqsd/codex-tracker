using System.Text;
using System.Text.Json;

namespace CodexTracker.Tests;

internal static class TestFixtures
{
    public static byte[] Auth(string email = "demo@example.test", string marker = "fixture", string? refreshMarker = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["email"] = email,
            ["https://api.openai.com/profile"] = new { email },
            ["https://api.openai.com/auth"] = new
            {
                chatgpt_account_id = "test-account-" + marker,
                chatgpt_plan_type = "plus"
            }
        };
        var jwt = "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture-signature";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new
            {
                access_token = jwt,
                refresh_token = "not-a-real-refresh-token-" + (refreshMarker ?? marker),
                id_token = jwt,
                account_id = "test-account-" + marker
            }
        });
    }
}

internal sealed class TestDirectory : IDisposable
{
    private static readonly string BasePath = Path.Combine(Path.GetTempPath(), "CodexTrackerTests");
    public string Root { get; } = Path.Combine(BasePath, Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Root);
    public string File(string name) => Path.Combine(Root, name);
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(Path.GetFullPath(BasePath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside the test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
