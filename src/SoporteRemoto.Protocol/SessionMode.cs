namespace SoporteRemoto.Protocol;

public enum SessionMode
{
    Screen = 0,
    Files = 1,
    Video = 2,
}

public static class SessionModeExtensions
{
    public static string ToWire(this SessionMode mode) => mode switch
    {
        SessionMode.Files => "files",
        SessionMode.Video => "video",
        _ => "screen",
    };

    public static SessionMode FromWire(string? value)
    {
        if (string.Equals(value, "files", StringComparison.OrdinalIgnoreCase))
            return SessionMode.Files;
        if (string.Equals(value, "video", StringComparison.OrdinalIgnoreCase))
            return SessionMode.Video;
        return SessionMode.Screen;
    }
}
