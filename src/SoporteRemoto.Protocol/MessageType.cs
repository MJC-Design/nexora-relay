namespace SoporteRemoto.Protocol;

public enum MessageType : byte
{
    Control = 1,
    ScreenFrame = 2,
    FileChunk = 3,
    AudioFrame = 4,
    VideoFrame = 5,
}

public static class ProtocolConstants
{
    public const int MaxFrameSize = 16 * 1024 * 1024;
    public const int DefaultLanPort = 51429;
    public const int DiscoveryPort = 51430;
}
