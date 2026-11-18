using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using SoporteRemoto.Android.Services;

namespace SoporteRemoto.Android;

[Activity(
    Label = "Nexora",
    Icon = "@drawable/icon",
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout |
                           ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        AndroidScreenSharing.UpdateScreenSize(this);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        ScreenShare.OnActivityResult(requestCode, resultCode, data);
        AndroidFiles.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        AndroidScreenSharing.UpdateScreenSize(this);
    }
}
