using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Android.Services;

/// <summary>
/// Captura la pantalla del teléfono con MediaProjection. Los fotogramas se
/// reducen a 900 px de ancho, se convierten a JPEG y se omiten si no cambian.
/// </summary>
public sealed class AndroidScreenCaptureBackend : IScreenCaptureBackend
{
    private const int MaxCaptureWidth = 900;
    private const int MaxFrames = 2;

    private readonly object _gate = new();
    private VirtualDisplay? _display;
    private ImageReader? _reader;
    private int _targetWidth;
    private int _targetHeight;
    private Bitmap? _bitmap;
    private int[]? _pixels;
    private byte[]? _row;
    private ulong _lastHash;
    private bool _hasHash;
    private bool _disposed;

    public int Width => AndroidScreenSharing.Width > 0 ? AndroidScreenSharing.Width : 1080;

    public int Height => AndroidScreenSharing.Height > 0 ? AndroidScreenSharing.Height : 1920;

    public byte[]? CaptureJpeg(int maxWidth, long quality)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (!AndroidScreenSharing.TryGet(out var projection, out var screenWidth, out var screenHeight, out var dpi))
                return null;

            var (captureWidth, captureHeight) = TargetSize(screenWidth, screenHeight);
            if (_display is null || _targetWidth != captureWidth || _targetHeight != captureHeight)
            {
                ReleaseDisplay();
                if (!CreateDisplay(projection!, captureWidth, captureHeight, dpi))
                    return null;
            }

            global::Android.Media.Image? image = null;
            try
            {
                image = _reader!.AcquireLatestImage();
                if (image is null) return null;

                var jpeg = Convert(image, (int)Math.Clamp(quality, 20, 90));
                image.Close();
                return jpeg;
            }
            catch
            {
                try { image?.Close(); } catch { }
                return null;
            }
        }
    }

    private static (int Width, int Height) TargetSize(int screenWidth, int screenHeight)
    {
        var width = Math.Min(MaxCaptureWidth, Math.Max(2, screenWidth));
        var height = Math.Max(2, (int)Math.Round(screenHeight * (double)width / screenWidth));
        width &= ~1;
        height &= ~1;
        return (Math.Max(2, width), Math.Max(2, height));
    }

    private bool CreateDisplay(MediaProjection projection, int width, int height, int dpi)
    {
        try
        {
            _reader = ImageReader.NewInstance(width, height, (ImageFormatType)1, MaxFrames);
            if (_reader is null) return false;

            _display = projection.CreateVirtualDisplay(
                "Nexora",
                width,
                height,
                Math.Max(160, dpi),
                (global::Android.Views.DisplayFlags)0,
                _reader.Surface,
                null,
                null);

            _targetWidth = width;
            _targetHeight = height;
            _pixels = new int[width * height];
            _row = new byte[width * 4 + 64];
            _hasHash = false;

            return _display is not null;
        }
        catch
        {
            ReleaseDisplay();
            return false;
        }
    }

    private byte[]? Convert(global::Android.Media.Image image, int quality)
    {
        var width = image.Width;
        var height = image.Height;
        var plane = image.GetPlanes()?[0];
        var buffer = plane?.Buffer;
        if (plane is null || buffer is null) return null;

        var rowStride = plane.RowStride;
        var pixelStride = plane.PixelStride;
        if (_pixels is null || _pixels.Length < width * height || _row is null || _row.Length < rowStride)
        {
            _pixels = new int[width * height];
            _row = new byte[rowStride];
        }

        var pixels = _pixels;
        var row = _row;
        buffer.Rewind();

        for (var y = 0; y < height; y++)
        {
            var remaining = buffer.Remaining();
            if (remaining <= 0) break;
            var toRead = Math.Min(rowStride, remaining);
            buffer.Get(row, 0, toRead);

            var offset = y * width;
            for (var x = 0; x < width; x++)
            {
                var i = x * pixelStride;
                if (i + 2 >= toRead) break;
                pixels[offset + x] = unchecked((int)0xFF000000)
                                     | ((row[i] & 0xFF) << 16)
                                     | ((row[i + 1] & 0xFF) << 8)
                                     | (row[i + 2] & 0xFF);
            }
        }

        if (!HasChanged(pixels, width * height)) return null;

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Recycle();
            _bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
            if (_bitmap is null) return null;
        }

        _bitmap.SetPixels(pixels, 0, width, 0, 0, width, height);

        using var stream = new MemoryStream(96 * 1024);
        if (!_bitmap.Compress(Bitmap.CompressFormat.Jpeg!, quality, stream)) return null;
        return stream.ToArray();
    }

    private bool HasChanged(int[] pixels, int count)
    {
        ulong hash = 14695981039346656037UL;
        for (var i = 0; i < count; i += 16)
        {
            var value = (uint)pixels[i];
            hash ^= value;
            hash *= 1099511628211UL;
            hash ^= value >> 16;
            hash *= 1099511628211UL;
        }

        if (_hasHash && hash == _lastHash) return false;
        _lastHash = hash;
        _hasHash = true;
        return true;
    }

    private void ReleaseDisplay()
    {
        try { _display?.Release(); } catch { }
        _display = null;

        try { _reader?.Close(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _reader = null;

        _targetWidth = 0;
        _targetHeight = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseDisplay();
            try { _bitmap?.Recycle(); } catch { }
            _bitmap = null;
            _pixels = null;
            _row = null;
        }
    }
}
