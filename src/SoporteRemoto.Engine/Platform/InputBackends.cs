using System.Runtime.InteropServices;

namespace SoporteRemoto.Engine;

public interface IInputBackend
{
    void Move(int x, int y);
    void Button(int button, bool down, int x, int y);
    void Wheel(int delta, int x, int y);
    void Key(int vk, bool down);
    void Text(string text);
    (int X, int Y) GetCursorPosition();
}

internal static class InputBackends
{
    public static IInputBackend Create()
    {
        if (PlatformHooks.Input is { } factory) return factory();
        if (OperatingSystem.IsWindows()) return new WindowsInputBackend();
        if (OperatingSystem.IsLinux()) return new LinuxInputBackend();
        if (OperatingSystem.IsMacOS()) return new MacInputBackend();
        throw new PlatformNotSupportedException("Remote control is not supported on this platform.");
    }
}

internal sealed class WindowsInputBackend : IInputBackend
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint WheelFlag = 0x0800;
    private const uint Absolute = 0x8000;

    private const uint KeyUp = 0x0002;
    private const uint ExtendedKey = 0x0001;
    private const uint Unicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private static (int Width, int Height) ScreenSize()
    {
        var w = Math.Max(1, GetSystemMetrics(0));
        var h = Math.Max(1, GetSystemMetrics(1));
        return (w, h);
    }

    private static (int Dx, int Dy) ToAbsolute(int x, int y)
    {
        var (w, h) = ScreenSize();
        var dx = (int)Math.Round(Math.Clamp(x, 0, w - 1) * 65535.0 / Math.Max(1, w - 1));
        var dy = (int)Math.Round(Math.Clamp(y, 0, h - 1) * 65535.0 / Math.Max(1, h - 1));
        return (dx, dy);
    }

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT MouseInput(uint flags, int x, int y, uint data = 0)
    {
        var (dx, dy) = ToAbsolute(x, y);
        return new INPUT
        {
            Type = InputMouse,
            U = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = data,
                    Flags = flags | MouseMove | Absolute,
                },
            },
        };
    }

    public void Move(int x, int y) => Send(MouseInput(0, x, y));

    public void Button(int button, bool down, int x, int y)
    {
        uint flag = button switch
        {
            1 => down ? RightDown : RightUp,
            2 => down ? MiddleDown : MiddleUp,
            _ => down ? LeftDown : LeftUp,
        };
        Send(MouseInput(flag, x, y));
    }

    public void Wheel(int delta, int x, int y) =>
        Send(MouseInput(WheelFlag, x, y, unchecked((uint)delta)));

    public void Key(int vk, bool down)
    {
        if (vk <= 0) return;
        uint flags = 0;
        if (!down) flags |= KeyUp;
        if (IsExtended(vk)) flags |= ExtendedKey;

        Send(new INPUT
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    Vk = (ushort)vk,
                    Scan = 0,
                    Flags = flags,
                },
            },
        });
    }

    public void Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var ch in text)
        {
            if (ch == '\n' || ch == '\r')
            {
                Key(0x0D, true);
                Key(0x0D, false);
                continue;
            }

            Send(new INPUT
            {
                Type = InputKeyboard,
                U = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
                    {
                        Vk = 0,
                        Scan = ch,
                        Flags = Unicode,
                    },
                },
            });
            Send(new INPUT
            {
                Type = InputKeyboard,
                U = new InputUnion
                {
                    Keyboard = new KEYBDINPUT
                    {
                        Vk = 0,
                        Scan = ch,
                        Flags = KeyUp | Unicode,
                    },
                },
            });
        }
    }

    private static bool IsExtended(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x2C or 0x90 or 0x91 or
        0xA3 or 0xA5 or 0x5B or 0x5C or 0x6F => true,
        _ => false,
    };

    public (int X, int Y) GetCursorPosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
}

internal sealed class LinuxInputBackend : IInputBackend
{
    private readonly object _gate = new();
    private IntPtr _display;

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? name);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XRootWindow(IntPtr display, int screen);

    [DllImport("libX11.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XQueryPointer(IntPtr display, IntPtr window,
        out IntPtr rootReturn, out IntPtr childReturn,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport("libX11.so.6")]
    private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, ulong delay);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int isPress, ulong delay);

    private IntPtr Display()
    {
        if (_display != IntPtr.Zero) return _display;
        try { _display = XOpenDisplay(null); } catch { _display = IntPtr.Zero; }
        return _display;
    }

    public void Move(int x, int y)
    {
        lock (_gate)
        {
            var d = Display();
            if (d == IntPtr.Zero) return;
            XTestFakeMotionEvent(d, -1, x, y, 0);
            XFlush(d);
        }
    }

    public void Button(int button, bool down, int x, int y)
    {
        lock (_gate)
        {
            var d = Display();
            if (d == IntPtr.Zero) return;
            XTestFakeMotionEvent(d, -1, x, y, 0);
            var xButton = button switch { 1 => 3u, 2 => 2u, _ => 1u };
            XTestFakeButtonEvent(d, xButton, down ? 1 : 0, 0);
            XFlush(d);
        }
    }

    public void Wheel(int delta, int x, int y)
    {
        lock (_gate)
        {
            var d = Display();
            if (d == IntPtr.Zero || delta == 0) return;
            XTestFakeMotionEvent(d, -1, x, y, 0);
            var button = delta > 0 ? 4u : 5u;
            var steps = Math.Max(1, Math.Abs(delta) / 120);
            for (var i = 0; i < steps; i++)
            {
                XTestFakeButtonEvent(d, button, 1, 0);
                XTestFakeButtonEvent(d, button, 0, 0);
            }
            XFlush(d);
        }
    }

    public void Key(int vk, bool down)
    {
        lock (_gate)
        {
            var d = Display();
            if (d == IntPtr.Zero || vk <= 0) return;
            var keysym = ToKeysym(vk);
            if (keysym == 0) return;
            var keycode = XKeysymToKeycode(d, keysym);
            if (keycode == 0) return;
            XTestFakeKeyEvent(d, keycode, down ? 1 : 0, 0);
            XFlush(d);
        }
    }

    public void Text(string text) => WriteAscii(text);

    private void WriteAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_gate)
        {
            var d = Display();
            if (d == IntPtr.Zero) return;

            foreach (var ch in text)
            {
                var keysym = ch switch
                {
                    '\n' or '\r' => 0xFF0DUL,
                    _ when ch >= ' ' && ch <= '~' => ch,
                    _ => 0UL,
                };
                if (keysym == 0) continue;

                var shift = char.IsUpper(ch) || "~!@#$%^&*()_+{}|:\"<>?".Contains(ch);
                var keycode = XKeysymToKeycode(d, keysym);
                var lower = char.ToLowerInvariant(ch);
                if (shift && lower != ch) keycode = XKeysymToKeycode(d, lower);
                if (keycode == 0) continue;

                var shiftCode = XKeysymToKeycode(d, 0xFFE1);
                if (shift) XTestFakeKeyEvent(d, shiftCode, 1, 0);
                XTestFakeKeyEvent(d, keycode, 1, 0);
                XTestFakeKeyEvent(d, keycode, 0, 0);
                if (shift) XTestFakeKeyEvent(d, shiftCode, 0, 0);
            }

            XFlush(d);
        }
    }

    public (int X, int Y) GetCursorPosition()
    {
        lock (_gate)
        {
            var d = Display();
            if (d == IntPtr.Zero) return (0, 0);
            try
            {
                var root = XRootWindow(d, XDefaultScreen(d));
                return XQueryPointer(d, root, out _, out _, out var x, out var y, out _, out _, out _)
                    ? (x, y)
                    : (0, 0);
            }
            catch
            {
                return (0, 0);
            }
        }
    }

    private static ulong ToKeysym(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (ulong)(0x61 + (vk - 0x41));
        if (vk >= 0x30 && vk <= 0x39) return (ulong)('0' + (vk - 0x30));
        if (vk >= 0x60 && vk <= 0x69) return (ulong)(0xFFB0 + (vk - 0x60));
        if (vk >= 0x70 && vk <= 0x87) return (ulong)(0xFFBE + (vk - 0x70));

        return vk switch
        {
            0x08 => 0xFF08,
            0x09 => 0xFF09,
            0x0D => 0xFF0D,
            0x10 => 0xFFE1,
            0x11 => 0xFFE3,
            0x12 => 0xFFE9,
            0x13 => 0xFF13,
            0x14 => 0xFFE5,
            0x1B => 0xFF1B,
            0x20 => 0x20,
            0x21 => 0xFF55,
            0x22 => 0xFF56,
            0x23 => 0xFF57,
            0x24 => 0xFF50,
            0x25 => 0xFF51,
            0x26 => 0xFF52,
            0x27 => 0xFF53,
            0x28 => 0xFF54,
            0x2C => 0xFF61,
            0x2D => 0xFF63,
            0x2E => 0xFFFF,
            0x5B => 0xFFEB,
            0x5C => 0xFFEC,
            0x5D => 0xFF67,
            0x6F => 0xFFAF,
            0x90 => 0xFF7F,
            0x91 => 0xFF14,
            0xA0 => 0xFFE1,
            0xA1 => 0xFFE1,
            0xA2 => 0xFFE3,
            0xA3 => 0xFFE3,
            0xA4 => 0xFFE9,
            0xA5 => 0xFFE9,
            0xBA => 0x3B,
            0xBB => 0x3D,
            0xBC => 0x2C,
            0xBD => 0x2D,
            0xBE => 0x2E,
            0xBF => 0x2F,
            0xC0 => 0x60,
            0xDB => 0x5B,
            0xDC => 0x5C,
            0xDD => 0x5D,
            0xDE => 0x27,
            _ => 0,
        };
    }
}

internal sealed class MacInputBackend : IInputBackend
{
    private const uint LeftDown = 1;
    private const uint LeftUp = 2;
    private const uint RightDown = 3;
    private const uint RightUp = 4;
    private const uint OtherDown = 25;
    private const uint OtherUp = 26;
    private const uint LeftDragged = 6;
    private const uint RightDragged = 7;
    private const uint OtherDragged = 27;
    private const uint HidEventTap = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
        public CGPoint(double x, double y) { X = x; Y = y; }
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern CGPoint CGEventGetLocation(IntPtr @event);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGWarpMouseCursorPosition(CGPoint point);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint position, uint mouseButton);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, uint units, uint wheelCount, int wheel1);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGEventPost(uint tap, IntPtr @event);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    private int _heldButton = -1;

    public void Move(int x, int y)
    {
        var point = new CGPoint(x, y);
        if (_heldButton >= 0)
        {
            var type = _heldButton switch
            {
                1 => RightDragged,
                2 => OtherDragged,
                _ => LeftDragged,
            };
            PostMouse(type, point, (uint)_heldButton);
        }
        else
        {
            try { CGWarpMouseCursorPosition(point); } catch { }
        }
    }

    public void Button(int button, bool down, int x, int y)
    {
        var point = new CGPoint(x, y);
        try
        {
            CGWarpMouseCursorPosition(point);
            var type = button switch
            {
                1 => down ? RightDown : RightUp,
                2 => down ? OtherDown : OtherUp,
                _ => down ? LeftDown : LeftUp,
            };
            PostMouse(type, point, (uint)button);
            _heldButton = down ? button : -1;
        }
        catch { }
    }

    public void Wheel(int delta, int x, int y)
    {
        if (delta == 0) return;
        try
        {
            CGWarpMouseCursorPosition(new CGPoint(x, y));
            var lines = (int)Math.Round(delta / 120.0);
            if (lines == 0) lines = delta > 0 ? 1 : -1;
            var evt = CGEventCreateScrollWheelEvent(IntPtr.Zero, 1, 1, lines);
            if (evt == IntPtr.Zero) return;
            try { CGEventPost(HidEventTap, evt); } finally { CFRelease(evt); }
        }
        catch { }
    }

    public void Key(int vk, bool down)
    {
        var keycode = ToMacKey(vk);
        if (keycode < 0) return;
        try
        {
            var evt = CGEventCreateKeyboardEvent(IntPtr.Zero, (ushort)keycode, down);
            if (evt == IntPtr.Zero) return;
            try { CGEventPost(HidEventTap, evt); } finally { CFRelease(evt); }
        }
        catch { }
    }

    public (int X, int Y) GetCursorPosition()
    {
        try
        {
            var evt = CGEventCreate(IntPtr.Zero);
            if (evt == IntPtr.Zero) return (0, 0);
            try
            {
                var p = CGEventGetLocation(evt);
                return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            }
            finally { CFRelease(evt); }
        }
        catch
        {
            return (0, 0);
        }
    }

    public void Text(string text)
    {
        // En macOS la escritura remota se limita a las teclas admitidas por el backend.
    }

    private static void PostMouse(uint type, CGPoint point, uint button)
    {
        var evt = CGEventCreateMouseEvent(IntPtr.Zero, type, point, button);
        if (evt == IntPtr.Zero) return;
        try { CGEventPost(HidEventTap, evt); } finally { CFRelease(evt); }
    }

    private static int ToMacKey(int vk) => vk switch
    {
        0x41 => 0, 0x53 => 1, 0x44 => 2, 0x46 => 3, 0x48 => 4, 0x47 => 5, 0x5A => 6, 0x58 => 7,
        0x43 => 8, 0x56 => 9, 0x42 => 11, 0x51 => 12, 0x57 => 13, 0x45 => 14, 0x52 => 15,
        0x59 => 16, 0x54 => 17, 0x31 => 18, 0x32 => 19, 0x33 => 20, 0x34 => 21, 0x36 => 22,
        0x35 => 23, 0x3D => 24, 0x39 => 25, 0x37 => 26, 0xBD => 27, 0x38 => 28, 0x30 => 29,
        0xDD => 30, 0x4F => 31, 0x55 => 32, 0xDB => 33, 0x49 => 34, 0x50 => 35, 0x0D => 36,
        0x4C => 37, 0x4A => 38, 0xDE => 39, 0x4B => 40, 0xBA => 41, 0xDC => 42, 0xBC => 43,
        0xBF => 44, 0x4E => 45, 0x4D => 46, 0xBE => 47, 0x09 => 48, 0x20 => 49, 0xC0 => 50,
        0x08 => 51, 0x1B => 53, 0x5B => 55, 0x5D => 110, 0x10 => 56, 0x14 => 57, 0x12 => 58, 0x11 => 59,
        0xA0 => 56, 0xA1 => 60, 0xA2 => 59, 0xA3 => 62, 0xA4 => 58, 0xA5 => 61,
        0x2E => 117, 0x2D => 114, 0x24 => 115, 0x21 => 116,
        0x23 => 119, 0x22 => 121, 0x25 => 123, 0x27 => 124, 0x28 => 125, 0x26 => 126,
        0x70 => 122, 0x71 => 120, 0x72 => 99, 0x73 => 118, 0x74 => 96, 0x75 => 97,
        0x76 => 98, 0x77 => 100, 0x78 => 101, 0x79 => 109, 0x7A => 103, 0x7B => 111,
        0x60 => 82, 0x61 => 83, 0x62 => 84, 0x63 => 85, 0x64 => 86, 0x65 => 87,
        0x66 => 88, 0x67 => 89, 0x68 => 91, 0x69 => 92, 0x6A => 93, 0x6B => 94,
        _ => -1,
    };
}
