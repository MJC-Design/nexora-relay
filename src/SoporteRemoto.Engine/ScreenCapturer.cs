namespace SoporteRemoto.Engine;

/// <summary>
/// Captura la pantalla principal y devuelve JPEG. La implementación depende del sistema operativo.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private readonly IScreenCaptureBackend _backend;

    public ScreenCapturer()
    {
        _backend = ScreenCaptureBackends.Create();
    }

    public int SourceWidth => _backend.Width;
    public int SourceHeight => _backend.Height;

    /// <summary>Captura la pantalla y devuelve un JPEG, o null si no cambió desde la última captura.</summary>
    public byte[]? CaptureJpeg(int maxWidth, long quality) => _backend.CaptureJpeg(maxWidth, quality);

    public void Dispose() => _backend.Dispose();
}
