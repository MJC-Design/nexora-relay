using System.Windows;

namespace SoporteRemoto.App;

public partial class App : Application
{
    public App()
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
