namespace SoporteRemoto.Engine;

/// <summary>
/// Cámara de la videollamada: captura fotogramas JPEG y notifica los errores
/// de forma que la interfaz pueda mostrarlos sin cerrar la llamada de audio.
/// </summary>
public sealed class VideoCall : IDisposable
{
    private readonly IVideoBackend _backend;
    private bool _running;

    public event Action<byte[]>? Captured;
    public event Action<string>? Error;

    public VideoCall()
    {
        _backend = VideoBackends.Create();
        _backend.Frame += OnFrame;
        _backend.Error += OnError;
    }

    public bool IsCapturing => _running;

    public void Start(MediaDeviceInfo? camera = null)
    {
        if (_running) return;
        _backend.Start(camera);
        _running = true;
    }

    /// <summary>Reinicia la captura usando otra cámara instalada.</summary>
    public void Restart(MediaDeviceInfo? camera)
    {
        _running = false;
        try { _backend.Stop(); } catch { }
        _backend.Start(camera);
        _running = true;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _backend.Stop(); } catch { }
    }

    public void Dispose()
    {
        _running = false;
        _backend.Frame -= OnFrame;
        _backend.Error -= OnError;
        try { _backend.Dispose(); } catch { }
    }

    private void OnFrame(byte[] jpeg)
    {
        try { Captured?.Invoke(jpeg); } catch { }
    }

    private void OnError(string message)
    {
        try { Error?.Invoke(message); } catch { }
    }
}
