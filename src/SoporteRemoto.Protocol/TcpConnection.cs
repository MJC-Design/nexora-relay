using System.Buffers.Binary;
using System.Net.Sockets;

namespace SoporteRemoto.Protocol;

public sealed class TcpConnection : IConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _closed;

    private TcpConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    public static async Task<TcpConnection> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            return new TcpConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static TcpConnection FromAccepted(TcpClient client) => new(client);

    public bool IsClosed => _closed || !_client.Connected;

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length + 1 > ProtocolConstants.MaxFrameSize)
            throw new InvalidOperationException("La trama es demasiado grande.");

        var buffer = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, payload.Length + 1);
        buffer[4] = (byte)type;
        payload.CopyTo(buffer.AsMemory(5));

        await _sendLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(buffer, ct);
            await _stream.FlushAsync(ct);
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
            var header = new byte[5];
            await _stream.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 1 || length > ProtocolConstants.MaxFrameSize)
                throw new InvalidDataException("Trama inválida.");

            var payload = new byte[length - 1];
            if (payload.Length > 0)
                await _stream.ReadExactlyAsync(payload, ct);

            return new ReceivedFrame((MessageType)header[4], payload);
        }
        catch (EndOfStreamException)
        {
            _closed = true;
            return null;
        }
        catch (IOException)
        {
            _closed = true;
            return null;
        }
        catch (SocketException)
        {
            _closed = true;
            return null;
        }
        catch (ObjectDisposedException)
        {
            _closed = true;
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
