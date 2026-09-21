using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexTracker.Codex;

internal sealed class AppServerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ChildProcessJob _job;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<object>>? _rereadDesktopTokens;
    private Task? _reader;
    private Task? _errorReader;
    private long _id;
    public bool IsStopped { get; private set; }

    private AppServerClient(Process process, Func<CancellationToken, Task<object>>? rereadDesktopTokens)
    { _process = process; _job = new ChildProcessJob(process); _rereadDesktopTokens = rereadDesktopTokens; }

    public static async Task<AppServerClient> StartAsync(string executable, string home, bool externalAuth,
        Func<CancellationToken, Task<object>>? rereadDesktopTokens, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = home
        };
        start.ArgumentList.Add("-c");
        // Compatibility parameter retained for callers; there is no managed-auth mode in the tracker.
        start.ArgumentList.Add("cli_auth_credentials_store=\"ephemeral\"");
        start.ArgumentList.Add("app-server");
        start.Environment["CODEX_HOME"] = home;
        // Isolate credentials and state even when the tracker inherits a developer shell environment.
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN",
            "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_WORKLOAD_IDENTITY_CONTEXT", "CODEX_SQLITE_HOME" })
            start.Environment.Remove(name);
        var process = Process.Start(start) ?? throw new TrackerException("Impossible de démarrer le service Codex.");
        AppServerClient client;
        try { client = new AppServerClient(process, rereadDesktopTokens); }
        catch
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
        client._reader = client.ReadAsync();
        // Discard stderr, which can contain session metadata, URLs or backend error bodies.
        client._errorReader = client.DrainErrorsAsync();
        try
        {
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex_tracker", title = "Codex Tracker", version = "0.5.4" },
                capabilities = new { experimentalApi = true }
            }, cancellationToken);
            await client.SendAsync(new { method = "initialized", @params = new { } }, cancellationToken);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        if (method == "account/read") parameters = new { refreshToken = false };
        else if (method == "account/login/start")
        {
            var supplied = JsonSerializer.SerializeToElement(parameters);
            if (AuthDocument.Read(supplied, "type") != "chatgptAuthTokens")
                throw new TrackerException("Codex Tracker accepte uniquement la session déjà ouverte dans Codex.");
            parameters = new
            {
                type = "chatgptAuthTokens", accessToken = AuthDocument.Read(supplied, "accessToken"),
                chatgptAccountId = AuthDocument.Read(supplied, "chatgptAccountId"),
                chatgptPlanType = AuthDocument.Read(supplied, "chatgptPlanType")
            };
        }
        else if (method is not "initialize" and not "account/rateLimits/read")
            throw new TrackerException("Cette opération ne fait pas partie du suivi en lecture seule.");
        var id = Interlocked.Increment(ref _id);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try { await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken); }
        finally { _write.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (line.Length > 4_000_000) throw new TrackerException("La réponse Codex dépasse la taille autorisée.");
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.TryGetProperty("method", out var method))
                {
                    var methodName = method.GetString() ?? "";
                    if (root.TryGetProperty("id", out var requestId))
                        await HandleServerRequestAsync(requestId.Clone(), methodName);
                }
                else if (root.TryGetProperty("id", out var id) && id.TryGetInt64(out var requestId) && _pending.TryRemove(requestId, out var completion))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var number) ? number : 0;
                        completion.TrySetException(new TrackerException($"Codex n'a pas pu répondre (RPC {code}). Actualisez ou vérifiez ce compte dans Codex."));
                    }
                    else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                    else completion.TrySetException(new TrackerException("Réponse Codex incompatible."));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or TrackerException or InvalidOperationException) { }
        finally
        {
            foreach (var completion in _pending.Values) completion.TrySetException(new TrackerException("Le service Codex s'est arrêté. Actualisez pour réessayer."));
        }
    }

    private async Task HandleServerRequestAsync(JsonElement id, string method)
    {
        if (method == "account/chatgptAuthTokens/refresh" && _rereadDesktopTokens is not null)
        {
            try
            {
                var supplied = JsonSerializer.SerializeToElement(await _rereadDesktopTokens(_lifetime.Token));
                var tokens = new
                {
                    accessToken = AuthDocument.Read(supplied, "accessToken"),
                    chatgptAccountId = AuthDocument.Read(supplied, "chatgptAccountId"),
                    chatgptPlanType = AuthDocument.Read(supplied, "chatgptPlanType")
                };
                await SendAsync(new { id, result = tokens }, _lifetime.Token);
                return;
            }
            catch (Exception ex) when (ex is IOException or TrackerException or OperationCanceledException or JsonException) { }
        }
        // This read-only client never approves tools or triggers a refresh for the desktop-owned session.
        await SendAsync(new { id, error = new { code = -32601, message = "Operation unavailable in Codex Tracker." } }, _lifetime.Token);
    }

    private async Task DrainErrorsAsync()
    {
        try { var buffer = new char[2048]; while (await _process.StandardError.ReadAsync(buffer, _lifetime.Token) != 0) Array.Clear(buffer); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { _process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { _process.Kill(true); } catch (InvalidOperationException) { }
            // Kill requests termination; credentials cannot be read until the writer has actually exited.
            using var killedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(killedTimeout.Token); }
            catch (OperationCanceledException) { }
        }
        IsStopped = _process.HasExited;
        _job.Dispose();
        if (_reader is not null) await _reader;
        if (_errorReader is not null) await _errorReader;
        _process.Dispose();
        _lifetime.Dispose();
        _write.Dispose();
        if (!IsStopped) throw new TrackerException("Le service Codex n'a pas quitté à temps. Redémarrez le tracker avant une nouvelle connexion.");
    }
}
