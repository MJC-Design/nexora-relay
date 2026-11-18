using SoporteRemoto.Engine;

namespace SoporteRemoto.Android.Services;

/// <summary>Audio y cámara quedan desactivados en esta versión de Android.</summary>
public sealed class SilentAudioBackend : IAudioBackend
{
    public event Action<byte[]>? Captured
    {
        add { }
        remove { }
    }

    public bool IsPlaying => false;
    public bool IsCapturing => false;

    public void EnsurePlayback() { }
    public bool TryStartCapture(MediaDeviceInfo? microphone) => false;
    public void StopCapture() { }
    public void Play(ReadOnlySpan<byte> data) { }
    public void StopAll() { }
    public void Dispose() { }
}

public sealed class SilentVideoBackend : IVideoBackend
{
    private bool _running;

    public event Action<byte[]>? Frame
    {
        add { }
        remove { }
    }

    public event Action<string>? Error;

    public bool IsCapturing => _running;

    public void Start(MediaDeviceInfo? camera)
    {
        _running = false;
        Error?.Invoke("La cámara no está disponible en esta versión de Android.");
    }

    public void Stop() => _running = false;

    public void Dispose() => Stop();
}
