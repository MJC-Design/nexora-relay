using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            Localization.Apply(settings.Language);
            desktop.MainWindow = new MainWindow(settings);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
