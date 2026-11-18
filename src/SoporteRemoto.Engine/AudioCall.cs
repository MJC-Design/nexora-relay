namespace SoporteRemoto.Engine;

/// <summary>
/// Llamada de audio de baja latencia: captura el micrófono (16 kHz, mono, 16 bits)
/// y reproduce los fragmentos que llegan del otro equipo.
/// </summary>
public sealed class AudioCall : IDisposable
{
    public const int SampleRate = 16000;

    private readonly IAudioBackend _backend;

    public event Action<byte[]>? Captured;

    public AudioCall()
    {
        _backend = AudioBackends.Create();
        _backend.Captured += OnCaptured;
    }

    public bool IsPlaying => _backend.IsPlaying;
    public bool IsCapturing => _backend.IsCapturing;

    public void EnsurePlayback() => _backend.EnsurePlayback();

    public bool TryStartCapture(MediaDeviceInfo? microphone = null) => _backend.TryStartCapture(microphone);

    /// <summary>Reinicia solo la captura con otro micrófono, sin cortar la reproducción.</summary>
    public void RestartCapture(MediaDeviceInfo? microphone)
    {
        _backend.StopCapture();
        if (!_backend.TryStartCapture(microphone))
            throw new InvalidOperationException("No se encontró ningún micrófono conectado.");
    }

    public void Play(ReadOnlySpan<byte> data) => _backend.Play(data);

    public void StopAll()
    {
        _backend.Captured -= OnCaptured;
        _backend.StopAll();
    }

    public void Dispose()
    {
        _backend.Captured -= OnCaptured;
        _backend.Dispose();
    }

    private void OnCaptured(byte[] data)
    {
        try { Captured?.Invoke(data); } catch { }
    }
}
