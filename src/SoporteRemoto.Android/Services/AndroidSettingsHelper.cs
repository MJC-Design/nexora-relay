using Android.Content;
using Android.Content.PM;

namespace SoporteRemoto.Android.Services;

public static class AndroidSettingsHelper
{
    public static void OpenAccessibility(Context context)
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionAccessibilitySettings);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
        }
        catch
        {
            // Algunos fabricantes no exponen esta pantalla directamente.
        }
    }
}
