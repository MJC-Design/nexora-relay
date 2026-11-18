using SoporteRemoto.Protocol;

namespace SoporteRemoto.Engine;

/// <summary>
/// Contrato común de las sesiones (equipo remoto y técnico) para la ventana
/// de videollamada, que se usa igual en modo exclusivo y durante el soporte.
/// </summary>
public interface IVideoSession
{
    string PeerName { get; }
    bool IsHost { get; }
    bool IsAudioLocal { get; }
    MediaDeviceInfo? SelectedCamera { get; }
    MediaDeviceInfo? SelectedMicrophone { get; }

    event Action<string, string?>? ChatReceived;
    event Action<bool, bool>? AudioStateChanged;
    event Action<string>? AudioError;
    event Action<bool, bool>? VideoStateChanged;
    event Action<byte[]>? LocalVideoFrame;
    event Action<byte[]>? RemoteVideoFrame;
    event Action<string>? VideoError;
    event Action? RemoteVideoEnded;
    event Action<string>? SessionEnded;

    Task SendChatAsync(string text);
    IReadOnlyList<MediaDeviceInfo> GetCameraDevices();
    IReadOnlyList<MediaDeviceInfo> GetMicrophoneDevices();
    bool SelectCamera(MediaDeviceInfo device);
    bool SelectMicrophone(MediaDeviceInfo device);
    void StartAudioCall();
    void StopAudioCall();
    void StartVideo();
    void StopVideo();
    Task EndSessionAsync();
}
