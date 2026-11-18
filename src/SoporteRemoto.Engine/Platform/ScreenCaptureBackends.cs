using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SoporteRemoto.Engine;

public interface IScreenCaptureBackend : IDisposable
{
    int Width { get; }
    int Height { get; }
    byte[]? CaptureJpeg(int maxWidth, long quality);
}

internal static class ScreenCaptureBackends
{
    public static IScreenCaptureBackend Create()
    {
        if (PlatformHooks.ScreenCapture is { } factory) return factory();
        if (OperatingSystem.IsWindows()) return new WindowsScreenCaptureBackend();
        if (OperatingSystem.IsLinux()) return new LinuxScreenCaptureBackend();
        if (OperatingSystem.IsMacOS()) return new MacScreenCaptureBackend();
        throw new PlatformNotSupportedException("Screen sharing is not supported on this platform.");
    }
}

/// <summary>Captura mediante GDI, disponible solo en Windows.</summary>
internal sealed class WindowsScreenCaptureBackend : IScreenCaptureBackend
{
    private readonly Bitmap _source;
    private readonly Graphics _sourceGraphics;
    private readonly object _gate = new();

    private Bitmap? _scaled;
    private Graphics? _scaledGraphics;
    private ulong _lastHash;
    private bool _hasHash;
    private bool _disposed;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public WindowsScreenCaptureBackend()
    {
        Width = Math.Max(1, GetSystemMetrics(0));
        Height = Math.Max(1, GetSystemMetrics(1));
        _source = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        _sourceGraphics = Graphics.FromImage(_source);
    }

    public int Width { get; }
    public int Height { get; }

    public byte[]? CaptureJpeg(int maxWidth, long quality)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            _sourceGraphics.CopyFromScreen(0, 0, 0, 0, new Size(Width, Height), CopyPixelOperation.SourceCopy);

            Bitmap target;
            if (Width <= maxWidth)
            {
                target = _source;
            }
            else
            {
                var w = maxWidth;
                var h = Math.Max(1, (int)Math.Round(Height * (double)maxWidth / Width));
                if (_scaled is null || _scaled.Width != w || _scaled.Height != h)
                {
                    _scaledGraphics?.Dispose();
                    _scaled?.Dispose();
                    _scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    _scaledGraphics = Graphics.FromImage(_scaled);
                    _scaledGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    _scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    _scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                }
                _scaledGraphics!.DrawImage(_source, new Rectangle(0, 0, w, h),
                    new Rectangle(0, 0, Width, Height), GraphicsUnit.Pixel);
                target = _scaled;
            }

            var hash = HashBitmap(target);
            if (_hasHash && hash == _lastHash)
                return null;
            _lastHash = hash;
            _hasHash = true;

            using var ms = new MemoryStream(256 * 1024);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 10, 95));
            target.Save(ms, JpegCodec, parameters);
            return ms.ToArray();
        }
    }

    private static unsafe ulong HashBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            ulong hash = 14695981039346656037UL;
            var ptr = (byte*)data.Scan0;
            var stride = data.Stride;
            for (var y = 0; y < bmp.Height; y++)
            {
                var row = ptr + y * stride;
                for (var i = 0; i < bmp.Width * 4; i += 4)
                {
                    hash ^= row[i];
                    hash *= 1099511628211UL;
                    hash ^= row[i + 1];
                    hash *= 1099511628211UL;
                    hash ^= row[i + 2];
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _scaledGraphics?.Dispose();
            _scaled?.Dispose();
            _sourceGraphics.Dispose();
            _source.Dispose();
        }
    }
}

/// <summary>
/// Captura mediante herramientas del sistema (grim/ffmpeg/import en Linux, screencapture en macOS).
/// </summary>
internal abstract class ExternalScreenCaptureBackend : IScreenCaptureBackend
{
    private readonly object _gate = new();
    private ulong _lastHash;
    private bool _hasHash;
    private bool _disposed;
    protected int _width;
    protected int _height;
    private byte[]? _primed;

    public int Width => _width;
    public int Height => _height;

    protected abstract byte[]? CapturePng();

    protected void SetPrimed(byte[]? png)
    {
        _primed = png;
        if (png is not null && SkiaJpeg.TryReadSize(png, out var w, out var h))
        {
            _width = w;
            _height = h;
        }
    }

    public byte[]? CaptureJpeg(int maxWidth, long quality)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            byte[]? png;
            if (_primed is not null)
            {
                png = _primed;
                _primed = null;
            }
            else
            {
                png = CapturePng();
            }

            if (png is null || png.Length == 0) return null;

            var hash = HashBytes(png);
            if (_hasHash && hash == _lastHash) return null;
            _lastHash = hash;
            _hasHash = true;

            var jpeg = SkiaJpeg.Convert(png, maxWidth, quality, out var w, out var h);
            if (jpeg is not null)
            {
                _width = w;
                _height = h;
            }
            return jpeg;
        }
    }

    private static ulong HashBytes(byte[] data)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }
}

internal sealed class LinuxScreenCaptureBackend : ExternalScreenCaptureBackend
{
    private readonly string? _display;
    private readonly bool _wayland;

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    public LinuxScreenCaptureBackend()
    {
        _display = Environment.GetEnvironmentVariable("DISPLAY");
        _wayland = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        try
        {
            var display = XOpenDisplay(null);
            if (display != IntPtr.Zero)
            {
                var screen = XDefaultScreen(display);
                var w = XDisplayWidth(display, screen);
                var h = XDisplayHeight(display, screen);
                XCloseDisplay(display);
                if (w > 0 && h > 0)
                {
                    _width = w;
                    _height = h;
                }
            }
        }
        catch
        {
            // Sin X11 (por ejemplo Wayland puro): el tamaño se detecta al capturar.
        }
    }

    protected override byte[]? CapturePng()
    {
        if (_wayland)
        {
            var grim = ProcessRunner.RunBinary("grim", "-t png -", 4000);
            if (grim is { Length: > 0 }) return grim;
        }

        if (!string.IsNullOrWhiteSpace(_display) && _width > 0 && _height > 0)
        {
            var args =
                $"-hide_banner -loglevel error -f x11grab -draw_mouse 0 -video_size {_width}x{_height} " +
                $"-i {_display}+0,0 -frames:v 1 -f image2pipe -vcodec png -";
            var ffmpeg = ProcessRunner.RunBinary("ffmpeg", args, 6000);
            if (ffmpeg is { Length: > 0 }) return ffmpeg;
        }

        var imported = ProcessRunner.RunBinary("import", "-silent -window root png:-", 6000);
        return imported is { Length: > 0 } ? imported : null;
    }
}

internal sealed class MacScreenCaptureBackend : ExternalScreenCaptureBackend
{
    private readonly string _tempFile = Path.Combine(
        Path.GetTempPath(), "nexora-screen-" + Environment.ProcessId + ".png");

    public MacScreenCaptureBackend()
    {
        SetPrimed(CapturePngCore());
    }

    protected override byte[]? CapturePng() => CapturePngCore();

    private byte[]? CapturePngCore()
    {
        try
        {
            var psi = new ProcessStartInfo("screencapture", $"-x -t png \"{_tempFile}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            if (!process.WaitForExit(6000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            if (!File.Exists(_tempFile)) return null;
            var bytes = File.ReadAllBytes(_tempFile);
            try { File.Delete(_tempFile); } catch { }
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}

internal static class SkiaJpeg
{
    public static bool TryReadSize(byte[] encoded, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = new MemoryStream(encoded);
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null) return false;
            width = bitmap.Width;
            height = bitmap.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static byte[]? Convert(byte[] encoded, int maxWidth, long quality, out int sourceWidth, out int sourceHeight)
    {
        sourceWidth = 0;
        sourceHeight = 0;
        try
        {
            using var stream = new MemoryStream(encoded);
            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null) return null;

            sourceWidth = bitmap.Width;
            sourceHeight = bitmap.Height;
            if (sourceWidth <= 0 || sourceHeight <= 0) return null;

            SKBitmap? resized = null;
            var target = bitmap;
            if (sourceWidth > maxWidth)
            {
                var w = Math.Max(1, maxWidth);
                var h = Math.Max(1, (int)Math.Round(sourceHeight * (double)maxWidth / sourceWidth));
                resized = bitmap.Resize(new SKImageInfo(w, h), SKFilterQuality.Medium);
                if (resized is not null) target = resized;
            }

            try
            {
                using var image = SKImage.FromBitmap(target);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, (int)Math.Clamp(quality, 10, 95));
                return data?.ToArray();
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }
}

internal static class ProcessRunner
{
    public static byte[]? RunBinary(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;

            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!copy.Wait(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            process.WaitForExit(2000);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static bool CommandExists(string command)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", $"-c \"command -v {command} >/dev/null 2>&1\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(2500)) return false;
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
