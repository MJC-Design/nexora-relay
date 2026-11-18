namespace SoporteRemoto.Engine;

/// <summary>
/// Inyecta ratón y teclado en el equipo local. La implementación depende del sistema operativo.
/// </summary>
public static class InputSender
{
    private static readonly Lazy<IInputBackend> Backend = new(InputBackends.Create);

    public static void Move(int x, int y) => Backend.Value.Move(x, y);

    public static void Button(int button, bool down, int x, int y) => Backend.Value.Button(button, down, x, y);

    public static void Wheel(int delta, int x, int y) => Backend.Value.Wheel(delta, x, y);

    public static void Key(int vk, bool down) => Backend.Value.Key(vk, down);

    public static void Text(string text) => Backend.Value.Text(text);

    public static (int X, int Y) GetCursorPosition() => Backend.Value.GetCursorPosition();
}
