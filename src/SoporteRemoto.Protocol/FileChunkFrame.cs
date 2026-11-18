using System.Buffers.Binary;

namespace SoporteRemoto.Protocol;

public static class FileChunkFrame
{
    // payload = [fileId:int32][datos]
    public static byte[] Build(int fileId, ReadOnlySpan<byte> data)
    {
        var payload = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, fileId);
        data.CopyTo(payload.AsSpan(4));
        return payload;
    }

    public static int ReadFileId(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : -1;
}
