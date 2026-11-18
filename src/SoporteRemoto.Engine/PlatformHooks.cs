namespace SoporteRemoto.Engine;

/// <summary>
/// Puntos de extensión para cabeceras de plataforma que no usan los backends
/// de escritorio (por ejemplo, la aplicación Android). La app los asigna al
/// arrancar, antes de crear sesiones.
/// </summary>
public static class PlatformHooks
{
    public static Func<IScreenCaptureBackend>? ScreenCapture { get; set; }
    public static Func<IInputBackend>? Input { get; set; }
    public static Func<IAudioBackend>? Audio { get; set; }
    public static Func<IVideoBackend>? Video { get; set; }
    public static Func<IReadOnlyList<MediaDeviceInfo>>? Cameras { get; set; }
    public static Func<IReadOnlyList<MediaDeviceInfo>>? Microphones { get; set; }
    public static Func<string>? DeviceName { get; set; }
}
