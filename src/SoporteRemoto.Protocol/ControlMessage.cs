using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoporteRemoto.Protocol;

public sealed class ControlMessage
{
    public string Type { get; set; } = "";

    // Autenticación (código de sesión de 9 caracteres)
    public string? Code { get; set; }

    // Modo de sesión: "screen" (pantalla y control), "files" (solo archivos) o "video" (videollamada)
    public string? Mode { get; set; }

    // Chat entre las dos personas
    public string? Text { get; set; }
    public string? From { get; set; }
    public string? ClientName { get; set; }
    public string? HostName { get; set; }
    public bool? Ok { get; set; }
    public string? Message { get; set; }
    public int? ScreenWidth { get; set; }
    public int? ScreenHeight { get; set; }
    public int? VideoWidth { get; set; }
    public int? VideoHeight { get; set; }

    // Entrada remota
    public string? InputType { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Button { get; set; }
    public bool? Down { get; set; }
    public int? Vk { get; set; }
    public int? WheelDelta { get; set; }

    // Cursor remoto
    public int? CursorX { get; set; }
    public int? CursorY { get; set; }

    // Archivos
    public int? FileId { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public bool? Accepted { get; set; }
    public string? Error { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static ControlMessage? FromUtf8(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<ControlMessage>(data, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
