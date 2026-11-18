namespace SoporteRemoto.Engine;

/// <summary>Nombre visible del equipo en las sesiones.</summary>
public static class DeviceInfo
{
    public static string Name
    {
        get
        {
            try
            {
                if (PlatformHooks.DeviceName is { } provider)
                {
                    var name = provider();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch
            {
                // Sin nombre de plataforma: se usa el del sistema.
            }

            return Environment.MachineName;
        }
    }
}
