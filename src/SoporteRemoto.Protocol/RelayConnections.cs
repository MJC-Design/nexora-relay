using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SoporteRemoto.Protocol;

public class RelayException : Exception
{
    public RelayException(string message) : base(message) { }
}

public sealed class RelayCodeTakenException : RelayException
{
    public RelayCodeTakenException() : base("El código ya está en uso.") { }
}

internal sealed class RelayTextMessage
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Message { get; set; }

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public static class RelayUri
{
    public static Uri Build(string serverUrl, string relativePath, string? query = null)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var baseUri))
            throw new RelayException("La URL del servidor no es válida.");

        var scheme = baseUri.Scheme.ToLowerInvariant() switch
        {
            "http" => "ws",
            "https" => "wss",
            "ws" => "ws",
            "wss" => "wss",
            _ => throw new RelayException("La URL del servidor debe empezar por http://, https://, ws:// o wss://"),
        };

        var path = "/" + relativePath.TrimStart('/');
        var builder = new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = path,
            Query = query ?? "",
        };
        return builder.Uri;
    }
}

internal static class WebSocketIo
{
    public static async Task SendTextAsync(WebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
    }

    public static async Task<string> ReceiveTextAsync(WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var msg = await ReceiveMessageAsync(ws, maxBytes, ct)
                  ?? throw new IOException("El servidor cerró la conexión.");
        if (msg.Type != WebSocketMessageType.Text)
            throw new RelayException("Respuesta inesperada del servidor.");
        return Encoding.UTF8.GetString(msg.Data);
    }

    public static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveMessageAsync(
        WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
            if (ms.Length > maxBytes)
                throw new InvalidDataException("Mensaje demasiado grande.");

            if (result.EndOfMessage)
                return (result.MessageType, ms.ToArray());
        }
    }
}

/// <summary>Conexión saliente del equipo que comparte su escritorio, a través del relay.</summary>
public sealed class RelayHostConnection : IConnection
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _closed;

    public string SessionId { get; }

    private RelayHostConnection(ClientWebSocket ws, string id)
    {
        _ws = ws;
        SessionId = id;
    }

    public bool IsClosed => _closed || _ws.State != WebSocketState.Open;

    public static async Task<RelayHostConnection> ConnectAsync(string serverUrl, string code, CancellationToken ct)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length != SessionCode.Length)
            throw new RelayException("El código de sesión no es válido.");

        var uri = RelayUri.Build(serverUrl, "ws/host");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(uri, ct);
            await WebSocketIo.SendTextAsync(ws,
                JsonSerializer.Serialize(new { type = "register", id = normalized }, RelayTextMessage.Options), ct);

            var json = await WebSocketIo.ReceiveTextAsync(ws, 64 * 1024, ct);
            var msg = JsonSerializer.Deserialize<RelayTextMessage>(json, RelayTextMessage.Options);
            if (msg?.Type == "taken")
                throw new RelayCodeTakenException();
            if (msg is null || msg.Type != "registered" || string.IsNullOrWhiteSpace(msg.Id))
                throw new RelayException(msg?.Message ?? "El servidor no confirmó el registro.");
            return new RelayHostConnection(ws, msg.Id);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var buffer = new byte[1 + payload.Length];
        buffer[0] = (byte)type;
        payload.CopyTo(buffer.AsMemory(1));

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open)
                throw new IOException("La conexión con el servidor está cerrada.");
            await _ws.SendAsync(buffer.AsMemory(), WebSocketMessageType.Binary, true, ct);
        }
        catch
        {
            _closed = true;
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<ReceivedFrame?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            while (true)
            {
                var msg = await WebSocketIo.ReceiveMessageAsync(_ws, ProtocolConstants.MaxFrameSize, ct);
                if (msg is null)
                {
                    _closed = true;
                    return null;
                }

                if (msg.Value.Type == WebSocketMessageType.Binary)
                {
                    var data = msg.Value.Data;
                    if (data.Length == 0) continue;
                    return new ReceivedFrame((MessageType)data[0], new ReadOnlyMemory<byte>(data, 1, data.Length - 1));
                }

                var text = Encoding.UTF8.GetString(msg.Value.Data);
                RelayTextMessage? relay = null;
                try { relay = JsonSerializer.Deserialize<RelayTextMessage>(text, RelayTextMessage.Options); }
                catch (JsonException) { }

                if (relay?.Type == "peerClosed")
                    return new ReceivedFrame(MessageType.Control,
                        new ControlMessage { Type = "disconnect", Message = "El equipo remoto cerró la conexión." }.ToUtf8());

                if (relay?.Type == "error")
                    return new ReceivedFrame(MessageType.Control,
                        new ControlMessage { Type = "error", Message = relay.Message ?? "Error del servidor." }.ToUtf8());
            }
        }
        catch (WebSocketException)
        {
            _closed = true;
            return null;
        }
        catch (IOException)
        {
            _closed = true;
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        _ws.Dispose();
        _sendLock.Dispose();
    }
}

/// <summary>Conexión del técnico hacia el equipo remoto, a través del relay.</summary>
public sealed class RelayClientConnection : IConnection
{
    private readonly ClientWebSocket _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _closed;

    private RelayClientConnection(ClientWebSocket ws) => _ws = ws;

    public bool IsClosed => _closed || _ws.State != WebSocketState.Open;

    public static async Task<RelayClientConnection> ConnectAsync(string serverUrl, string code, CancellationToken ct)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length != SessionCode.Length)
            throw new RelayException("El código de sesión no es válido.");

        var uri = RelayUri.Build(serverUrl, "ws/client", "id=" + Uri.EscapeDataString(normalized));
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(uri, ct);
            return new RelayClientConnection(ws);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var buffer = new byte[1 + payload.Length];
        buffer[0] = (byte)type;
        payload.CopyTo(buffer.AsMemory(1));

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open)
                throw new IOException("La conexión con el servidor está cerrada.");
            await _ws.SendAsync(buffer.AsMemory(), WebSocketMessageType.Binary, true, ct);
        }
        catch
        {
            _closed = true;
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<ReceivedFrame?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            while (true)
            {
                var msg = await WebSocketIo.ReceiveMessageAsync(_ws, ProtocolConstants.MaxFrameSize, ct);
                if (msg is null)
                {
                    _closed = true;
                    return null;
                }

                if (msg.Value.Type == WebSocketMessageType.Binary)
                {
                    var data = msg.Value.Data;
                    if (data.Length == 0) continue;
                    return new ReceivedFrame((MessageType)data[0], new ReadOnlyMemory<byte>(data, 1, data.Length - 1));
                }

                var text = Encoding.UTF8.GetString(msg.Value.Data);
                RelayTextMessage? relay = null;
                try { relay = JsonSerializer.Deserialize<RelayTextMessage>(text, RelayTextMessage.Options); }
                catch (JsonException) { }

                if (relay?.Type == "peerClosed")
                    return new ReceivedFrame(MessageType.Control,
                        new ControlMessage { Type = "disconnect", Message = "La sesión fue cerrada." }.ToUtf8());

                if (relay?.Type == "error")
                    return new ReceivedFrame(MessageType.Control,
                        new ControlMessage { Type = "error", Message = relay.Message ?? "Error del servidor." }.ToUtf8());
            }
        }
        catch (WebSocketException)
        {
            _closed = true;
            return null;
        }
        catch (IOException)
        {
            _closed = true;
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        _ws.Dispose();
        _sendLock.Dispose();
    }
}
