using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoporteRemoto.Protocol;

var builder = WebApplication.CreateBuilder(args);

var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    var port = Environment.GetEnvironmentVariable("PORT");
    configuredUrls = string.IsNullOrWhiteSpace(port)
        ? "http://0.0.0.0:5080"
        : $"http://0.0.0.0:{port}";
}
builder.WebHost.UseUrls(configuredUrls);

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

var hosts = new ConcurrentDictionary<string, HostEntry>();
var lifetime = app.Lifetime.ApplicationStopping;

app.MapGet("/", () => Results.Text("SoporteRemoto: servidor relay activo."));
app.MapGet("/api/health", () => Results.Json(new { status = "ok", hosts = hosts.Count }));

app.Map("/ws/host", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();

    string code;
    try
    {
        using var regTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        regTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var text = await ReceiveTextAsync(socket, regTimeout.Token);
        var requested = text is null
            ? null
            : JsonSerializer.Deserialize<HostRegistration>(text, RelayJson.Options)?.Id;
        code = SessionCode.Normalize(requested);
        if (code.Length != SessionCode.Length)
        {
            await RejectAsync(socket, "Código de registro no válido.");
            return;
        }
    }
    catch
    {
        try { socket.Abort(); } catch { }
        return;
    }

    var entry = new HostEntry(code, socket);
    if (!hosts.TryAdd(code, entry))
    {
        try { await SendTextAsync(socket, "{\"type\":\"taken\"}", CancellationToken.None); } catch { }
        try { socket.Abort(); } catch { }
        return;
    }

    Log($"Equipo registrado: {code}");

    try
    {
        await SendTextAsync(socket, JsonSerializer.Serialize(new { type = "registered", id = code }), lifetime);

        _ = HostReceiverLoopAsync(entry, hosts, lifetime);

        while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
        {
            var client = await entry.WaitForClientAsync(lifetime);
            if (client is null) break;

            await ClientPumpAsync(entry, client, lifetime);

            // El técnico se desconectó: el equipo sigue registrado para otra sesión.
            entry.EndSession();

            try { await SendTextAsync(socket, "{\"type\":\"peerClosed\"}", CancellationToken.None); } catch { }
            try
            {
                if (client.State == WebSocketState.Open)
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "fin de sesión", CancellationToken.None);
            }
            catch
            {
                try { client.Abort(); } catch { }
            }

            Log($"Sesión finalizada en {code}");
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Log($"Equipo {code}: {ex.Message}");
    }
    finally
    {
        hosts.TryRemove(code, out _);
        entry.EndSession();
        entry.FailWaiters();
        try { socket.Abort(); } catch { }
        Log($"Equipo desconectado: {code}");
    }
});

app.Map("/ws/client", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var id = SessionCode.Normalize(context.Request.Query["id"].ToString());
    var socket = await context.WebSockets.AcceptWebSocketAsync();

    if (id.Length != SessionCode.Length || !hosts.TryGetValue(id, out var entry))
    {
        await RejectAsync(socket, "No hay ningún equipo en línea con ese código.");
        return;
    }

    if (!entry.TryAttachClient(socket, out var sessionEnded))
    {
        await RejectAsync(socket, "El equipo remoto ya está en una sesión.");
        return;
    }

    Log($"Técnico conectado a {id}");

    try
    {
        await sessionEnded.WaitAsync(lifetime);
    }
    catch (OperationCanceledException) { }
    catch { }
    finally
    {
        try
        {
            if (socket.State == WebSocketState.Open) socket.Abort();
        }
        catch { }
        Log($"Técnico desconectado de {id}");
    }
});

app.Run();

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

static async Task HostReceiverLoopAsync(
    HostEntry entry, ConcurrentDictionary<string, HostEntry> hosts, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (entry.Host.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await entry.Host.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var client = entry.Client;
            if (client is null || client.State != WebSocketState.Open) continue;

            try
            {
                await SendToAsync(client, buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
            catch
            {
                // El bucle del técnico detectará el cierre.
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Log($"Recepción del equipo {entry.Id}: {ex.Message}");
    }
    finally
    {
        hosts.TryRemove(entry.Id, out _);
        entry.FailWaiters();
        entry.EndSession();
        try { entry.Host.Abort(); } catch { }
    }
}

static async Task ClientPumpAsync(HostEntry entry, WebSocket client, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (!ct.IsCancellationRequested
               && client.State == WebSocketState.Open
               && entry.Host.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            await SendToAsync(entry.Host, buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch { }
}

static async Task SendTextAsync(WebSocket socket, string text, CancellationToken ct)
{
    var bytes = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
}

static async Task SendToAsync(
    WebSocket socket, ReadOnlyMemory<byte> data, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
{
    await socket.SendAsync(data, type, endOfMessage, ct);
}

static async Task RejectAsync(WebSocket socket, string message)
{
    try
    {
        await SendTextAsync(socket, JsonSerializer.Serialize(new { type = "error", message }), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "error", timeout.Token);
    }
    catch
    {
        try { socket.Abort(); } catch { }
    }
}

static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
{
    var buffer = new byte[8 * 1024];
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
        if (result.MessageType != WebSocketMessageType.Text) return null;

        ms.Write(buffer, 0, result.Count);
        if (ms.Length > 16 * 1024) return null;
        if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
    }
}

internal sealed class HostRegistration
{
    public string? Type { get; set; }
    public string? Id { get; set; }
}

internal static class RelayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal sealed class HostEntry
{
    private readonly object _gate = new();
    private WebSocket? _client;
    private TaskCompletionSource<WebSocket?> _waiter = NewWaiter();
    private TaskCompletionSource<bool>? _sessionEnded;

    public string Id { get; }
    public WebSocket Host { get; }

    public HostEntry(string id, WebSocket host)
    {
        Id = id;
        Host = host;
    }

    public WebSocket? Client
    {
        get
        {
            lock (_gate) return _client;
        }
    }

    public bool TryAttachClient(WebSocket client, out Task sessionEnded)
    {
        lock (_gate)
        {
            if (_client is not null || Host.State != WebSocketState.Open)
            {
                sessionEnded = Task.CompletedTask;
                return false;
            }

            _client = client;
            _sessionEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sessionEnded = _sessionEnded.Task;
            _waiter.TrySetResult(client);
            return true;
        }
    }

    public async Task<WebSocket?> WaitForClientAsync(CancellationToken ct)
    {
        Task<WebSocket?> waiter;
        lock (_gate) waiter = _waiter.Task;
        return await waiter.WaitAsync(ct);
    }

    public void EndSession()
    {
        TaskCompletionSource<bool>? ended;
        lock (_gate)
        {
            _client = null;
            ended = _sessionEnded;
            _sessionEnded = null;
            _waiter = NewWaiter();
        }
        ended?.TrySetResult(true);
    }

    public void FailWaiters()
    {
        lock (_gate) _waiter.TrySetResult(null);
    }

    private static TaskCompletionSource<WebSocket?> NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
