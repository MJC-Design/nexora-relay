using System.Diagnostics;
using OpenCvSharp;

namespace SoporteRemoto.Engine;

public interface IVideoBackend : IDisposable
{
    event Action<byte[]>? Frame;
    event Action<string>? Error;
    bool IsCapturing { get; }
    void Start(MediaDeviceInfo? camera);
    void Stop();
}

internal static class VideoBackends
{
    public static IVideoBackend Create()
    {
        if (PlatformHooks.Video is { } factory) return factory();
        if (OperatingSystem.IsWindows()) return new WindowsVideoBackend();
        return new FfmpegVideoBackend();
    }
}

/// <summary>
/// Cámara en Windows mediante OpenCV (DirectShow con reserva a Media Foundation).
/// Los fotogramas se envían ya comprimidos en JPEG.
/// </summary>
internal sealed class WindowsVideoBackend : IVideoBackend
{
    private const int MaxWidth = 480;
    private const int TargetFps = 10;
    private const int JpegQuality = 62;

    private readonly object _gate = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private bool _running;

    public event Action<byte[]>? Frame;
    public event Action<string>? Error;

    public bool IsCapturing => _running;

    public void Start(MediaDeviceInfo? camera)
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            var index = 0;
            if (camera is not null && int.TryParse(camera.Id, out var parsed) && parsed >= 0)
                index = parsed;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _thread = new Thread(() => CaptureLoop(index, ct))
            {
                IsBackground = true,
                Name = "NexoraCamera",
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            thread = _thread;
            cts = _cts;
            _thread = null;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(1500);
        cts?.Dispose();
    }

    private void CaptureLoop(int cameraIndex, CancellationToken ct)
    {
        VideoCapture? capture = null;
        try
        {
            foreach (var api in new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY })
            {
                var candidate = new VideoCapture(cameraIndex, api);
                if (candidate.IsOpened())
                {
                    capture = candidate;
                    break;
                }
                candidate.Dispose();
            }

            if (capture is null)
            {
                Error?.Invoke("No se encontró ninguna cámara conectada.");
                return;
            }

            using var frame = new Mat();
            using var scaled = new Mat();
            var interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
            var next = Stopwatch.GetTimestamp();

            while (!ct.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(40);
                    continue;
                }

                var wait = interval - Stopwatch.GetElapsedTime(next);
                if (wait > TimeSpan.FromMilliseconds(3))
                    Thread.Sleep(wait);
                next = Stopwatch.GetTimestamp();

                var source = frame;
                if (frame.Width > MaxWidth)
                {
                    var width = MaxWidth;
                    var height = Math.Max(1, (int)Math.Round(frame.Height * (double)MaxWidth / frame.Width));
                    Cv2.Resize(frame, scaled, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
                    source = scaled;
                }

                Cv2.ImEncode(".jpg", source, out var jpeg,
                    new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, JpegQuality) });
                if (jpeg.Length > 0)
                {
                    try { Frame?.Invoke(jpeg); } catch { }
                }
            }
        }
        catch (DllNotFoundException ex)
        {
            Error?.Invoke("No se pudo iniciar la cámara: " + ex.Message);
        }
        catch (Exception ex)
        {
            Error?.Invoke("No se pudo iniciar la cámara: " + ex.Message);
        }
        finally
        {
            capture?.Dispose();
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Cámara en Linux y macOS mediante ffmpeg (v4l2 / avfoundation),
/// igual que el audio usa parec/arecord o ffmpeg+ffplay.
/// </summary>
internal sealed class FfmpegVideoBackend : IVideoBackend
{
    private const int TargetFps = 10;
    private const int MaxWidth = 480;

    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private bool _running;

    public event Action<byte[]>? Frame;
    public event Action<string>? Error;

    public bool IsCapturing => _running;

    public void Start(MediaDeviceInfo? camera)
    {
        lock (_gate)
        {
            if (_running) return;

            var ffmpeg = FfmpegLocator.Path;
            if (ffmpeg is null)
                throw new InvalidOperationException("No se encontró ffmpeg. Instálalo para usar la videollamada.");

            var psi = new ProcessStartInfo(ffmpeg, BuildArguments(camera))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar ffmpeg.");

            var cts = new CancellationTokenSource();
            _process = process;
            _cts = cts;
            _running = true;

            var stdout = process.StandardOutput.BaseStream;
            var stderr = process.StandardError;
            _ = Task.Run(() => ReadFramesAsync(stdout, stderr, process, cts.Token));
        }
    }

    private static string BuildArguments(MediaDeviceInfo? camera)
    {
        var device = camera?.Id ?? Environment.GetEnvironmentVariable("NEXORA_CAMERA");
        string input;
        if (OperatingSystem.IsLinux())
            input = $"-f v4l2 -i {Quote(string.IsNullOrWhiteSpace(device) ? "/dev/video0" : device)}";
        else
            input = $"-f avfoundation -framerate 15 -i {Quote(string.IsNullOrWhiteSpace(device) ? "0" : device)}";

        return "-hide_banner -loglevel error -nostdin -fflags nobuffer -flags low_delay " +
               $"{input} -vf fps={TargetFps},scale={MaxWidth}:-2 -c:v mjpeg -q:v 6 -f mjpeg -";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private async Task ReadFramesAsync(Stream stdout, StreamReader stderr, Process process, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var frame = new MemoryStream();
        var inFrame = false;
        var previous = -1;
        var delivered = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer, ct);
                if (read <= 0) break;

                for (var i = 0; i < read; i++)
                {
                    var value = buffer[i];
                    if (!inFrame)
                    {
                        if (previous == 0xFF && value == 0xD8)
                        {
                            frame.SetLength(0);
                            frame.WriteByte(0xFF);
                            frame.WriteByte(0xD8);
                            inFrame = true;
                            previous = -1;
                            continue;
                        }
                        previous = value;
                        continue;
                    }

                    frame.WriteByte(value);
                    if (previous == 0xFF && value == 0xD9)
                    {
                        var jpeg = frame.ToArray();
                        frame.SetLength(0);
                        inFrame = false;
                        delivered++;
                        try { Frame?.Invoke(jpeg); } catch { }
                    }
                    previous = value;
                }
            }

            if (!ct.IsCancellationRequested && delivered == 0)
            {
                var detail = "";
                try { detail = (await stderr.ReadToEndAsync(ct)).Trim(); } catch { }
                Error?.Invoke("No se pudo iniciar la cámara: " +
                              (detail.Length > 0 ? detail : "ffmpeg finalizó sin enviar video."));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Error?.Invoke("No se pudo iniciar la cámara: " + ex.Message);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
        }
    }

    public void Stop()
    {
        Process? process;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            process = _process;
            cts = _cts;
            _process = null;
            _cts = null;
        }

        try { cts?.Cancel(); } catch { }
        cts?.Dispose();

        if (process is not null)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();
}

internal static class FfmpegLocator
{
    private static readonly Lazy<string?> Cached = new(Find);

    public static string? Path => Cached.Value;

    private static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new List<string>();

        var explicitPath = Environment.GetEnvironmentVariable("NEXORA_FFMPEG");
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);

        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            candidates.Add(System.IO.Path.Combine(root, name));
            candidates.Add(System.IO.Path.Combine(root, "ffmpeg", name));
            candidates.Add(System.IO.Path.Combine(root, "tools", name));
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathVariable.Split(System.IO.Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(System.IO.Path.Combine(directory.Trim('"'), name));

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.AddRange(new[]
            {
                System.IO.Path.Combine(local, "Microsoft", "WinGet", "Links", name),
                System.IO.Path.Combine(local, "Programs", "ffmpeg", "bin", name),
                System.IO.Path.Combine(programFiles, "ffmpeg", "bin", name),
                @"C:\ffmpeg\bin\ffmpeg.exe",
            });
        }
        else
        {
            candidates.AddRange(new[]
            {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/opt/homebrew/bin/ffmpeg",
                "/snap/bin/ffmpeg",
            });
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        return null;
    }
}
