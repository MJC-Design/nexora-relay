using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace SoporteRemoto.Android.Services;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection)]
public sealed class ScreenShareService : Service
{
    private const string ChannelId = "nexora_screen_share";
    private const int NotificationId = 7310;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
            else
                StartForeground(NotificationId, notification);
        }
        catch
        {
            // Sin permiso de notificaciones el servicio sigue funcionando.
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        AndroidScreenSharing.Clear();
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            var channel = new NotificationChannel(ChannelId, "Nexora", NotificationImportance.Low)
            {
                Description = "Captura de pantalla compartida",
            };
            manager?.CreateNotificationChannel(channel);
        }

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        return builder
            .SetContentTitle("Nexora")
            .SetContentText("Compartiendo la pantalla del teléfono")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuShare)
            .SetOngoing(true)
            .Build()!;
    }
}
