using Android.Content;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Android.Services;

/// <summary>Puente entre el motor y el servicio de accesibilidad que toca la pantalla.</summary>
public static class AndroidRemoteInput
{
    public static Func<int, int, bool>? Tap { get; internal set; }
    public static Func<int, int, int, int, int, bool>? Swipe { get; internal set; }
    public static Func<int, int, bool>? LongPress { get; internal set; }
    public static Func<int, bool>? GlobalAction { get; internal set; }
    public static Func<string, bool>? TypeText { get; internal set; }

    public static bool IsAvailable => Tap is not null;

    public static bool IsEnabled(Context context)
    {
        try
        {
            var expected = context.PackageName + "/" +
                           Java.Lang.Class.FromType(typeof(NexoraAccessibilityService)).Name;
            var enabled = global::Android.Provider.Settings.Secure.GetString(
                context.ContentResolver, global::Android.Provider.Settings.Secure.EnabledAccessibilityServices);
            return enabled is not null && enabled.Contains(expected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void Attach(NexoraAccessibilityService service)
    {
        Tap = service.Tap;
        Swipe = service.Swipe;
        LongPress = service.LongPress;
        GlobalAction = service.RunGlobalAction;
        TypeText = service.TypeText;
    }

    internal static void Detach()
    {
        Tap = null;
        Swipe = null;
        LongPress = null;
        GlobalAction = null;
        TypeText = null;
    }
}

/// <summary>
/// Traduce los eventos de ratón del motor a gestos de Android. Un clic es un
/// toque, mantener pulsado equivale a pulsación larga y arrastrar es un gesto.
/// </summary>
public sealed class AndroidInputBackend : IInputBackend
{
    private const int TapThreshold = 24;
    private const int LongPressMs = 650;

    private int _pressX;
    private int _pressY;
    private long _pressAt;
    private bool _pressed;
    private int _lastX;
    private int _lastY;

    public void Move(int x, int y)
    {
        _lastX = x;
        _lastY = y;
    }

    public void Button(int button, bool down, int x, int y)
    {
        if (button == 2) return;

        if (button == 1)
        {
            if (down) AndroidRemoteInput.LongPress?.Invoke(x, y);
            return;
        }

        if (down)
        {
            _pressed = true;
            _pressX = x;
            _pressY = y;
            _pressAt = Environment.TickCount64;
            return;
        }

        if (!_pressed) return;
        _pressed = false;

        var distance = (int)Math.Sqrt(
            Math.Pow(x - _pressX, 2) + Math.Pow(y - _pressY, 2));
        var duration = Environment.TickCount64 - _pressAt;

        if (distance <= TapThreshold)
        {
            if (duration >= LongPressMs)
                AndroidRemoteInput.LongPress?.Invoke(x, y);
            else
                AndroidRemoteInput.Tap?.Invoke(x, y);
            return;
        }

        AndroidRemoteInput.Swipe?.Invoke(_pressX, _pressY, x, y,
            (int)Math.Clamp(duration, 80, 1500));
    }

    public void Wheel(int delta, int x, int y)
    {
        if (delta == 0) return;
        var direction = delta > 0 ? 1 : -1;
        AndroidRemoteInput.Swipe?.Invoke(x, y - 60 * direction, x, y + 60 * direction, 240);
    }

    public void Key(int vk, bool down)
    {
        if (!down) return;
        var action = vk switch
        {
            27 or 0xA6 => 1, // Escape / atrás
            36 or 0xAC => 2, // Inicio
            _ => 0,
        };
        if (action > 0) AndroidRemoteInput.GlobalAction?.Invoke(action);
    }

    public void Text(string text)
    {
        if (!string.IsNullOrEmpty(text))
            AndroidRemoteInput.TypeText?.Invoke(text);
    }

    public (int X, int Y) GetCursorPosition() => (_lastX, _lastY);
}
