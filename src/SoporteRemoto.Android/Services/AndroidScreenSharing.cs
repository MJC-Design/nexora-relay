using Android.App;
using Android.Content;
using Android.Hardware.Display;
using Android.Media.Projection;
using Android.OS;
using Android.Views;

namespace SoporteRemoto.Android.Services;

public static class AndroidScreenSharing
{
    private static readonly object Gate = new();
    private static MediaProjection? _projection;
    private static int _width;
    private static int _height;
    private static int _dpi;

    public static int Width { get { lock (Gate) return _width; } }
    public static int Height { get { lock (Gate) return _height; } }

    public static bool IsReady
    {
        get
        {
            lock (Gate) return _projection is not null && _width > 0 && _height > 0;
        }
    }

    public static void SetProjection(MediaProjection projection, int width, int height, int dpi)
    {
        MediaProjection? previous;
        lock (Gate)
        {
            previous = _projection;
            _projection = projection;
            _width = width;
            _height = height;
            _dpi = dpi;
        }

        if (!ReferenceEquals(previous, projection))
        {
            try { previous?.Stop(); } catch { }
        }

        projection.RegisterCallback(new StopCallback(), new Handler(Looper.MainLooper!));
    }

    public static void UpdateScreenSize(Context context)
    {
        var (width, height, dpi) = Metrics(context);
        if (width <= 0 || height <= 0) return;
        lock (Gate)
        {
            _width = width;
            _height = height;
            _dpi = dpi;
        }
    }

    public static void Clear()
    {
        MediaProjection? projection;
        lock (Gate)
        {
            projection = _projection;
            _projection = null;
            _width = 0;
            _height = 0;
            _dpi = 0;
        }

        try { projection?.Stop(); } catch { }
    }

    public static bool TryGet(out MediaProjection? projection, out int width, out int height, out int dpi)
    {
        lock (Gate)
        {
            projection = _projection;
            width = _width;
            height = _height;
            dpi = _dpi;
            return projection is not null && width > 0 && height > 0;
        }
    }

    public static (int Width, int Height, int Dpi) Metrics(Context context)
    {
        try
        {
            var resources = context.Resources?.DisplayMetrics;
            var dpi = resources is not null && resources.DensityDpi > 0
                ? (int)resources.DensityDpi
                : 420;

            if (resources is not null && resources.WidthPixels > 0)
                return (resources.WidthPixels, resources.HeightPixels, dpi);
        }
        catch
        {
            // Sin servicio de ventanas: se usan valores de reserva.
        }

        return (1080, 1920, 420);
    }

    private sealed class StopCallback : MediaProjection.Callback
    {
        public override void OnStop()
        {
            lock (Gate)
            {
                _projection = null;
                _width = 0;
                _height = 0;
                _dpi = 0;
            }
        }
    }
}
