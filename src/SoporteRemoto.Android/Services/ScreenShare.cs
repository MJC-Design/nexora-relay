using Android.App;
using Android.Content;
using Android.Media.Projection;
using Android.OS;

namespace SoporteRemoto.Android.Services;

public static class ScreenShare
{
    public const int RequestCode = 4251;

    private static TaskCompletionSource<Intent?>? _pending;

    public static async Task<bool> StartAsync(Activity activity)
    {
        var data = await RequestConsentAsync(activity);
        if (data is null) return false;

        var manager = (MediaProjectionManager?)activity.GetSystemService(Context.MediaProjectionService);
        if (manager is null) return false;

        try
        {
            var service = new Intent(activity, typeof(ScreenShareService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                activity.StartForegroundService(service);
            else
                activity.StartService(service);

            // El servicio debe estar en primer plano antes de crear la proyección (Android 14).
            await Task.Delay(350);

            var projection = manager.GetMediaProjection((int)Result.Ok, data);
            if (projection is null)
            {
                StopService(activity);
                return false;
            }

            var (width, height, dpi) = AndroidScreenSharing.Metrics(activity);
            AndroidScreenSharing.SetProjection(projection, width, height, dpi);
            return true;
        }
        catch
        {
            StopService(activity);
            return false;
        }
    }

    public static void Stop(Activity activity)
    {
        StopService(activity);
        AndroidScreenSharing.Clear();
    }

    private static void StopService(Context context)
    {
        try { context.StopService(new Intent(context, typeof(ScreenShareService))); } catch { }
    }

    private static Task<Intent?> RequestConsentAsync(Activity activity)
    {
        var tcs = new TaskCompletionSource<Intent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        try
        {
            var manager = (MediaProjectionManager?)activity.GetSystemService(Context.MediaProjectionService);
            if (manager is null)
            {
                tcs.TrySetResult(null);
                return tcs.Task;
            }

            activity.StartActivityForResult(manager.CreateScreenCaptureIntent(), RequestCode);
        }
        catch
        {
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    internal static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode) return;

        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(resultCode == Result.Ok ? data : null);
    }
}
