namespace SoporteRemoto.Protocol;

public readonly record struct ReceivedFrame(MessageType Type, ReadOnlyMemory<byte> Payload);

public interface IConnection : IAsyncDisposable
{
    bool IsClosed { get; }

    Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    Task<ReceivedFrame?> ReceiveAsync(CancellationToken ct = default);
}
