using System.Text.Json;

namespace SoporteRemoto.Engine;

public sealed class AppSettings
{
    public string RelayUrl { get; set; } = "ws://localhost:5080";
    public string Language { get; set; } = "";
    public bool AllowConnections { get; set; } = true;

    /// <summary>Carpeta de datos de la app; Android la sustituye por su almacenamiento interno.</summary>
    public static Func<string>? StorageDirectory { get; set; }

    private static string FilePath => Path.Combine(
        StorageDirectory?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SoporteRemoto", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Ajustes corruptos: se regeneran con los valores por defecto.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Sin permisos de escritura: los ajustes no se persisten.
        }
    }
}
