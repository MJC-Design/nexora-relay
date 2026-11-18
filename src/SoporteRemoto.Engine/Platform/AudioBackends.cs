using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Wave;

namespace SoporteRemoto.Engine;

public interface IAudioBackend : IDisposable
{
    event Action<byte[]>? Captured;
    bool IsPlaying { get; }
    bool IsCapturing { get; }
    void EnsurePlayback();
    bool TryStartCapture(MediaDeviceInfo? microphone);
    void StopCapture();
    void Play(ReadOnlySpan<byte> data);
    void StopAll();
}

internal static class AudioBackends
{
    public static IAudioBackend Create()
    {
        if (PlatformHooks.Audio is { } factory) return factory();
        if (OperatingSystem.IsWindows()) return new WindowsAudioBackend();
        if (OperatingSystem.IsLinux()) return new LinuxAudioBackend();
        if (OperatingSystem.IsMacOS()) return new MacAudioBackend();
        throw new PlatformNotSupportedException("Audio calls are not supported on this platform.");
    }
}

internal sealed class WindowsAudioBackend : IAudioBackend
{
    private readonly object _gate = new();
    private WaveInEvent? _mic;
    private WaveOutEvent? _speaker;
    private BufferedWaveProvider? _playback;

    public event Action<byte[]>? Captured;

    public bool IsPlaying { get; private set; }
    public bool IsCapturing { get; private set; }

    public void EnsurePlayback()
    {
        lock (_gate)
        {
            if (_speaker is not null) return;

            _playback = new BufferedWaveProvider(new WaveFormat(AudioCall.SampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };
            _speaker = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
            _speaker.Init(_playback);
            _speaker.Play();
            IsPlaying = true;
        }
    }

    public bool TryStartCapture(MediaDeviceInfo? microphone)
    {
        lock (_gate)
        {
            if (_mic is not null) return true;
            if (WaveInEvent.DeviceCount <= 0) return false;

            var number = 0;
            if (microphone is not null && int.TryParse(microphone.Id, out var parsed))
                number = Math.Clamp(parsed, 0, WaveInEvent.DeviceCount - 1);

            var mic = new WaveInEvent
            {
                DeviceNumber = number,
                WaveFormat = new WaveFormat(AudioCall.SampleRate, 16, 1),
                BufferMilliseconds = 30,
                NumberOfBuffers = 3,
            };
            mic.DataAvailable += OnDataAvailable;
            mic.StartRecording();
            _mic = mic;
            IsCapturing = true;
            return true;
        }
    }

    public void StopCapture()
    {
        WaveInEvent? mic;
        lock (_gate)
        {
            mic = _mic;
            _mic = null;
            IsCapturing = false;
        }
        if (mic is null) return;
        try { mic.DataAvailable -= OnDataAvailable; } catch { }
        try { mic.StopRecording(); } catch { }
        try { mic.Dispose(); } catch { }
    }

    public void Play(ReadOnlySpan<byte> data)
    {
        BufferedWaveProvider? playback;
        lock (_gate) playback = _playback;
        if (playback is null || data.Length == 0) return;

        if (playback.BufferedDuration > TimeSpan.FromMilliseconds(350))
            playback.ClearBuffer();

        var buffer = data.ToArray();
        playback.AddSamples(buffer, 0, buffer.Length);
    }

    public void StopAll()
    {
        WaveInEvent? mic;
        WaveOutEvent? speaker;
        lock (_gate)
        {
            mic = _mic;
            speaker = _speaker;
            _mic = null;
            _speaker = null;
            _playback = null;
            IsCapturing = false;
            IsPlaying = false;
        }

        if (mic is not null)
        {
            try { mic.DataAvailable -= OnDataAvailable; } catch { }
            try { mic.StopRecording(); } catch { }
            try { mic.Dispose(); } catch { }
        }

        if (speaker is not null)
        {
            try { speaker.Stop(); } catch { }
            try { speaker.Dispose(); } catch { }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);
        try { Captured?.Invoke(data); } catch { }
    }

    public void Dispose() => StopAll();
}

/// <summary>
/// Audio mediante procesos del sistema: parec/pacat o arecord/aplay en Linux, ffmpeg/ffplay en macOS.
/// </summary>
internal abstract class ProcessAudioBackend : IAudioBackend
{
    private readonly object _gate = new();
    private Process? _mic;
    private Process? _speaker;
    private CancellationTokenSource? _micCts;
    private Channel<byte[]>? _playback;
    private bool _disposed;

    public event Action<byte[]>? Captured;

    public bool IsPlaying { get; private set; }
    public bool IsCapturing { get; private set; }

    protected abstract (string File, string Arguments)? CaptureCommand(string? device);
    protected abstract (string File, string Arguments)? PlaybackCommand();
    protected abstract string CaptureUnavailableMessage { get; }
    protected abstract string PlaybackUnavailableMessage { get; }

    public void EnsurePlayback()
    {
        lock (_gate)
        {
            if (_speaker is not null) return;

            var command = PlaybackCommand()
                ?? throw new InvalidOperationException(PlaybackUnavailableMessage);
            var psi = new ProcessStartInfo(command.File, command.Arguments)
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException(PlaybackUnavailableMessage);

            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            _speaker = process;
            _playback = channel;
            IsPlaying = true;

            var stdin = process.StandardInput.BaseStream;
            _ = Task.Run(() => WriteLoopAsync(stdin, channel.Reader));
        }
    }

    public bool TryStartCapture(MediaDeviceInfo? microphone)
    {
        lock (_gate)
        {
            if (_mic is not null) return true;

            var command = CaptureCommand(microphone?.Id)
                ?? throw new InvalidOperationException(CaptureUnavailableMessage);
            var psi = new ProcessStartInfo(command.File, command.Arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException(CaptureUnavailableMessage);

            var cts = new CancellationTokenSource();
            _mic = process;
            _micCts = cts;
            IsCapturing = true;

            var stdout = process.StandardOutput.BaseStream;
            _ = Task.Run(() => ReadLoopAsync(stdout, cts.Token));
            return true;
        }
    }

    public void Play(ReadOnlySpan<byte> data)
    {
        Channel<byte[]>? playback;
        lock (_gate) playback = _playback;
        if (playback is null || data.Length == 0) return;
        playback.Writer.TryWrite(data.ToArray());
    }

    public void StopCapture()
    {
        Process? mic;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            mic = _mic;
            cts = _micCts;
            _mic = null;
            _micCts = null;
            IsCapturing = false;
        }

        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
        StopProcess(mic);
    }

    public void StopAll()
    {
        Process? mic;
        Process? speaker;
        Channel<byte[]>? playback;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            mic = _mic;
            speaker = _speaker;
            playback = _playback;
            cts = _micCts;
            _mic = null;
            _speaker = null;
            _playback = null;
            _micCts = null;
            IsCapturing = false;
            IsPlaying = false;
        }

        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
        playback?.Writer.TryComplete();

        StopProcess(mic);
        StopProcess(speaker);
    }

    private static void StopProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }
                process.Kill(true);
            }
        }
        catch { }
        try { process.Dispose(); } catch { }
    }

    private async Task ReadLoopAsync(Stream stdout, CancellationToken ct)
    {
        var buffer = new byte[3200];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer, ct);
                if (read <= 0) break;
                var data = new byte[read];
                Buffer.BlockCopy(buffer, 0, data, 0, read);
                try { Captured?.Invoke(data); } catch { }
            }
        }
        catch { }
    }

    private static async Task WriteLoopAsync(Stream stdin, ChannelReader<byte[]> reader)
    {
        try
        {
            await foreach (var data in reader.ReadAllAsync())
            {
                await stdin.WriteAsync(data);
                await stdin.FlushAsync();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }
}

internal sealed class LinuxAudioBackend : ProcessAudioBackend
{
    protected override (string File, string Arguments)? CaptureCommand(string? device) =>
        ProcessRunner.CommandExists("parec")
            ? ("parec", "--format=s16le --rate=16000 --channels=1 --latency-msec=30" +
                (string.IsNullOrWhiteSpace(device) ? "" : $" --device={Quote(device)}"))
            : ProcessRunner.CommandExists("arecord")
                ? ("arecord", "-q -t raw -f S16_LE -r 16000 -c 1" +
                    (string.IsNullOrWhiteSpace(device) ? "" : $" -D {Quote(device)}"))
                : null;

    protected override (string File, string Arguments)? PlaybackCommand() =>
        ProcessRunner.CommandExists("pacat")
            ? ("pacat", "--playback --format=s16le --rate=16000 --channels=1 --latency-msec=150")
            : ProcessRunner.CommandExists("aplay")
                ? ("aplay", "-q -t raw -f S16_LE -r 16000 -c 1")
                : null;

    protected override string CaptureUnavailableMessage =>
        "No microphone tool found. Install PulseAudio (parec) or ALSA (arecord).";

    protected override string PlaybackUnavailableMessage =>
        "No audio playback tool found. Install PulseAudio (pacat) or ALSA (aplay).";

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}

internal sealed class MacAudioBackend : ProcessAudioBackend
{
    protected override (string File, string Arguments)? CaptureCommand(string? device) =>
        ProcessRunner.CommandExists("ffmpeg")
            ? ("ffmpeg", "-hide_banner -loglevel error -f avfoundation -i \":"
                + (string.IsNullOrWhiteSpace(device) ? "0" : device) + "\" -ac 1 -ar 16000 -f s16le -")
            : null;

    protected override (string File, string Arguments)? PlaybackCommand() =>
        ProcessRunner.CommandExists("ffplay")
            ? ("ffplay", "-hide_banner -loglevel quiet -f s16le -ar 16000 -ac 1 -nodisp -autoexit -")
            : null;

    protected override string CaptureUnavailableMessage =>
        "ffmpeg was not found. Install it (brew install ffmpeg) to use audio calls.";

    protected override string PlaybackUnavailableMessage =>
        "ffplay was not found. Install ffmpeg (brew install ffmpeg) to use audio calls.";
}
