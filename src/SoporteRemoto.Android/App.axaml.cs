using Android.OS;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SoporteRemoto.Android.Services;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Android;

public partial class App : Avalonia.Application
{
    public static AppSettings Settings { get; private set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigurePlatform();

        Settings = AppSettings.Load();
        Settings.RelayUrl ??= "";
        if (Settings.RelayUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            Settings.RelayUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            Settings.RelayUrl = "";
            Settings.Save();
        }

        Localization.Apply(Settings.Language);

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MainView(Settings);

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigurePlatform()
    {
        PlatformHooks.ScreenCapture = static () => new AndroidScreenCaptureBackend();
        PlatformHooks.Input = static () => new AndroidInputBackend();
        PlatformHooks.Audio = static () => new SilentAudioBackend();
        PlatformHooks.Video = static () => new SilentVideoBackend();
        PlatformHooks.Cameras = static () => Array.Empty<MediaDeviceInfo>();
        PlatformHooks.Microphones = static () => Array.Empty<MediaDeviceInfo>();
        PlatformHooks.DeviceName = static () =>
        {
            var model = Build.Model ?? "Android";
            var maker = Build.Manufacturer ?? "";
            return maker.Length > 0 && !model.StartsWith(maker, StringComparison.OrdinalIgnoreCase)
                ? $"{maker} {model}"
                : model;
        };

        var context = global::Android.App.Application.Context;
        AppSettings.StorageDirectory = () => context.FilesDir?.AbsolutePath ?? context.CacheDir?.AbsolutePath ?? ".";
    }
}
