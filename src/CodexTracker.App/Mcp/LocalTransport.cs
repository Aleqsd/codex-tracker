using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace CodexTracker.App.Mcp;

internal static class Wire
{
    internal const int Protocol = 1;
    internal static string PipeName(string[] args)
    {
        var suffix = "";
        if (args.Contains("--demo"))
        {
            var index = Array.IndexOf(args, "--demo-instance");
            if (index < 0 || index + 1 >= args.Length || !Guid.TryParse(args[index + 1], out var id)) throw new ArgumentException("Instance de démonstration requise.");
            suffix = ".Demo." + id.ToString("N");
        }
        return $"CodexTracker.Mcp.v1.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}{suffix}";
    }
    internal static async Task Write(Stream stream, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, TrackerControl.Json);
        if (bytes.Length > 1024 * 1024) throw new IOException("Message trop volumineux.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    internal static async Task<JsonElement> Read(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BitConverter.ToInt32(header);
        if (length is < 2 or > 1024 * 1024) throw new IOException("Message invalide.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 32 }); return doc.RootElement.Clone();
    }
}

internal sealed class LocalServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clients = new();
    private readonly MainWindow _window;
    private readonly TrackerControl _control;
    private readonly Task _listen;
    internal LocalServer(MainWindow window, TrackerControl control, string[] args)
    { _window = window; _control = control; _listen = Listen(Wire.PipeName(args)); }
    private async Task Listen(string name)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_clients.Count >= 15) { await Task.Delay(100, _stop.Token); continue; }
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                _clients[pipe] = Task.CompletedTask;
                _clients[pipe] = Serve(pipe);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    private async Task Serve(NamedPipeServerStream pipe)
    {
        await _window.Dispatcher.InvokeAsync(() => _control.Connections++);
        try
        {
            await Wire.Write(pipe, new { protocol = Wire.Protocol }, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                var request = await Wire.Read(pipe, _stop.Token);
                object response;
                try
                {
                    var method = request.GetProperty("method").GetString()!;
                    var arguments = request.GetProperty("arguments");
                    var result = await (await _window.Dispatcher.InvokeAsync(() => _control.HandleAsync(method, arguments)));
                    response = new { result };
                }
                catch (Exception e)
                {
                    // Never expose exception messages from parsers/providers: they may include submitted credentials.
                    response = new { error = e is InvalidOperationException && e.Message.StartsWith("mcp_disabled") ? "mcp_disabled"
                        : e is InvalidOperationException && e.Message is "revision_conflict" or "request_id_conflict" ? e.Message : "invalid_or_unavailable", detail = "Relisez l’état et vérifiez les paramètres ou les réglages Assistants." };
                }
                await Wire.Write(pipe, response, _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally { pipe.Dispose(); _clients.TryRemove(pipe, out _); await _window.Dispatcher.InvokeAsync(() => _control.Connections--); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); foreach (var pipe in _clients.Keys) pipe.Dispose();
        await _listen;
        await Task.WhenAll(_clients.Values);
    }
}

internal sealed class LocalClient(string[] args) : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentQueue<TaskCompletionSource<JsonElement>> _replies = new();
    private bool _started;
    internal CancellationToken Closed => _closed.Token;
    internal Task Start(CancellationToken token) => Connect(token);
    internal async Task<JsonElement> Call(string method, object arguments, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_closed.IsCancellationRequested) throw new IOException("Tracker déconnecté ; reconnectez le client MCP.");
            if (_pipe is null) await Connect(token);
            var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); _replies.Enqueue(reply);
            await Wire.Write(_pipe!, new { method, arguments }, token);
            // On cancellation, close this connection rather than consuming the next request's response.
            var result = await reply.Task.WaitAsync(token);
            return result;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        { _pipe?.Dispose(); if (_started) _closed.Cancel(); throw; }
        finally { _gate.Release(); }
    }
    private async Task Connect(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var name = Wire.PipeName(args); bool launched = false;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(250, timeout.Token);
                var hello = await Wire.Read(pipe, timeout.Token);
                if (hello.GetProperty("protocol").GetInt32() != Wire.Protocol) throw new IOException("Version incompatible.");
                _pipe = pipe; _started = true; _ = ReadReplies(); return;
            }
            catch (TimeoutException) { pipe.Dispose(); }
            catch { pipe.Dispose(); throw; }
            if (!launched)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--background"); start.ArgumentList.Add("--assistant-start");
                if (args.Contains("--demo"))
                {
                    start.ArgumentList.Add("--demo"); start.ArgumentList.Add("--demo-instance"); start.ArgumentList.Add(args[Array.IndexOf(args, "--demo-instance") + 1]);
                }
                using var process = Process.Start(start); launched = true;
            }
        }
    }
    private async Task ReadReplies()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var response = await Wire.Read(_pipe!, _closed.Token);
                if (!_replies.TryDequeue(out var reply)) throw new IOException("Réponse inattendue.");
                reply.TrySetResult(response);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        { while (_replies.TryDequeue(out var reply)) reply.TrySetException(new IOException("Tracker déconnecté.")); _closed.Cancel(); }
    }
    public ValueTask DisposeAsync() { _pipe?.Dispose(); _closed.Cancel(); return ValueTask.CompletedTask; }
}
