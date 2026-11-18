using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace SoporteRemoto.Engine;

/// <summary>
/// Dispositivo de captura (cámara o micrófono) instalado en el equipo.
/// Id es el identificador nativo que espera el backend correspondiente.
/// </summary>
public sealed record MediaDeviceInfo(int Index, string Name, string Id)
{
    public override string ToString() => Name;
}

/// <summary>
/// Enumeración de cámaras y micrófonos del sistema, con la última elección
/// recordada mientras la aplicación esté abierta.
/// </summary>
public static class MediaDevices
{
    public static MediaDeviceInfo? DefaultCamera { get; set; }
    public static MediaDeviceInfo? DefaultMicrophone { get; set; }

    public static IReadOnlyList<MediaDeviceInfo> GetCameras()
    {
        try
        {
            if (PlatformHooks.Cameras is { } provider) return provider();
            if (OperatingSystem.IsWindows()) return WindowsCameras();
            if (OperatingSystem.IsLinux()) return LinuxCameras();
            if (OperatingSystem.IsMacOS()) return MacDevices().Cameras;
        }
        catch { }
        return Array.Empty<MediaDeviceInfo>();
    }

    public static IReadOnlyList<MediaDeviceInfo> GetMicrophones()
    {
        try
        {
            if (PlatformHooks.Microphones is { } provider) return provider();
            if (OperatingSystem.IsWindows()) return WindowsMicrophones();
            if (OperatingSystem.IsLinux()) return LinuxMicrophones();
            if (OperatingSystem.IsMacOS()) return MacDevices().Mics;
        }
        catch { }
        return Array.Empty<MediaDeviceInfo>();
    }

    private static List<MediaDeviceInfo> WindowsCameras()
    {
        var names = DirectShowCameras.GetNames();
        var list = new List<MediaDeviceInfo>(names.Count);
        for (var i = 0; i < names.Count; i++)
            list.Add(new MediaDeviceInfo(i, names[i], i.ToString()));
        return list;
    }

    private static List<MediaDeviceInfo> WindowsMicrophones()
    {
        var list = new List<MediaDeviceInfo>();
        var count = WaveInEvent.DeviceCount;
        for (var i = 0; i < count; i++)
        {
            string name;
            try { name = WaveInEvent.GetCapabilities(i).ProductName; }
            catch { name = ""; }
            if (string.IsNullOrWhiteSpace(name)) name = $"Micrófono {i + 1}";
            list.Add(new MediaDeviceInfo(i, name, i.ToString()));
        }
        return list;
    }

    private static List<MediaDeviceInfo> LinuxCameras()
    {
        var list = new List<MediaDeviceInfo>();
        var devices = new List<(int Number, string Path)>();
        foreach (var path in Directory.GetFiles("/dev", "video*"))
        {
            var suffix = System.IO.Path.GetFileName(path)["video".Length..];
            if (int.TryParse(suffix, out var number))
                devices.Add((number, path));
        }

        devices.Sort((a, b) => a.Number.CompareTo(b.Number));
        foreach (var (number, path) in devices)
        {
            string name;
            try { name = File.ReadAllText($"/sys/class/video4linux/video{number}/name").Trim(); }
            catch { name = ""; }
            if (string.IsNullOrWhiteSpace(name)) name = $"Cámara {list.Count + 1}";
            list.Add(new MediaDeviceInfo(list.Count, name, path));
        }
        return list;
    }

    private static List<MediaDeviceInfo> LinuxMicrophones()
    {
        var list = new List<MediaDeviceInfo>();
        if (ProcessRunner.CommandExists("pactl"))
        {
            var descriptions = PulseDescriptions();
            var output = RunText("pactl", "list short sources");
            if (output is not null)
            {
                foreach (var line in output.Split('\n'))
                {
                    var fields = line.Trim().Split('\t');
                    if (fields.Length < 2) continue;
                    var id = fields[1].Trim();
                    if (id.Length == 0 || id.Contains(".monitor", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = descriptions.TryGetValue(id, out var description) && description.Length > 0
                        ? description
                        : id;
                    list.Add(new MediaDeviceInfo(list.Count, name, id));
                }
            }
            if (list.Count > 0) return list;
        }

        if (ProcessRunner.CommandExists("arecord"))
        {
            var output = RunText("arecord", "-l");
            if (output is not null)
            {
                foreach (var line in output.Split('\n'))
                {
                    var match = Regex.Match(line.Trim(),
                        @"^card (\d+): [^,]+, device (\d+): (.+)$", RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    var id = $"plughw:{match.Groups[1].Value},{match.Groups[2].Value}";
                    list.Add(new MediaDeviceInfo(list.Count, match.Groups[3].Value.Trim(), id));
                }
            }
        }
        return list;
    }

    private static Dictionary<string, string> PulseDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = RunText("pactl", "list sources");
        if (output is null) return map;

        string? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name: ", StringComparison.Ordinal))
                current = line["Name: ".Length..].Trim();
            else if (current is not null && line.StartsWith("Description: ", StringComparison.Ordinal))
            {
                map[current] = line["Description: ".Length..].Trim();
                current = null;
            }
        }
        return map;
    }

    private static (List<MediaDeviceInfo> Cameras, List<MediaDeviceInfo> Mics) _macCache;
    private static DateTime _macCacheAt;

    private static (List<MediaDeviceInfo> Cameras, List<MediaDeviceInfo> Mics) MacDevices()
    {
        if (DateTime.UtcNow - _macCacheAt < TimeSpan.FromSeconds(20))
            return _macCache;

        var cameras = new List<MediaDeviceInfo>();
        var mics = new List<MediaDeviceInfo>();
        var ffmpeg = FfmpegLocator.Path;
        if (ffmpeg is not null)
        {
            var output = RunText(ffmpeg, "-hide_banner -f avfoundation -list_devices true -i \"\"");
            if (output is not null)
            {
                var section = 0;
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("AVFoundation video devices", StringComparison.Ordinal))
                    {
                        section = 1;
                        continue;
                    }
                    if (line.Contains("AVFoundation audio devices", StringComparison.Ordinal))
                    {
                        section = 2;
                        continue;
                    }

                    var match = Regex.Match(line, @"\[(\d+)\]\s+(.+)$", RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    var index = match.Groups[1].Value;
                    var name = match.Groups[2].Value.Trim();
                    if (section == 1)
                        cameras.Add(new MediaDeviceInfo(cameras.Count, name, index));
                    else if (section == 2)
                        mics.Add(new MediaDeviceInfo(mics.Count, name, index));
                }
            }
        }

        _macCache = (cameras, mics);
        _macCacheAt = DateTime.UtcNow;
        return _macCache;
    }

    private static string? RunText(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            var stderr = stderrTask.Wait(1000) ? stderrTask.Result : "";
            return stdout + stderr;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Enumeración de cámaras en Windows mediante DirectShow (mismo orden de
/// índices que usa OpenCV con el backend DSHOW, sin abrir los dispositivos).
/// </summary>
internal static class DirectShowCameras
{
    private static readonly Guid SystemDeviceEnum =
        new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");

    private static readonly Guid VideoInputDeviceCategory =
        new("860BB310-5D01-11D0-BD3B-00A0C911CE86");

    public static List<string> GetNames()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;

        object? enumeratorObject = null;
        IEnumMoniker? enumMoniker = null;
        try
        {
            var type = Type.GetTypeFromCLSID(SystemDeviceEnum);
            if (type is null) return names;
            enumeratorObject = Activator.CreateInstance(type);
            if (enumeratorObject is not ICreateDevEnum devEnum) return names;

            var category = VideoInputDeviceCategory;
            if (devEnum.CreateClassEnumerator(ref category, out enumMoniker, 0) != 0 || enumMoniker is null)
                return names;

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (moniker is null) continue;
                try
                {
                    var name = ReadFriendlyName(moniker);
                    if (string.IsNullOrWhiteSpace(name)) name = $"Cámara {names.Count + 1}";
                    names.Add(name);
                }
                finally
                {
                    try { Marshal.ReleaseComObject(moniker); } catch { }
                }
            }
        }
        catch { }
        finally
        {
            if (enumMoniker is not null) { try { Marshal.ReleaseComObject(enumMoniker); } catch { } }
            if (enumeratorObject is not null) { try { Marshal.ReleaseComObject(enumeratorObject); } catch { } }
        }

        return names;
    }

    private static string? ReadFriendlyName(IMoniker moniker)
    {
        object? bagObject = null;
        try
        {
            var bagId = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null, null, ref bagId, out bagObject);
            if (bagObject is not IPropertyBag bag) return null;

            object value = "";
            return bag.Read("FriendlyName", ref value, IntPtr.Zero) == 0 ? value as string : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bagObject is not null) { try { Marshal.ReleaseComObject(bagObject); } catch { } }
        }
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid type, out IEnumMoniker? enumMoniker, int flags);
    }

    [ComImport]
    [Guid("00000102-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumMoniker
    {
        [PreserveSig]
        int Next(int count,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface)] IMoniker[] monikers,
            IntPtr fetched);
    }

    [ComImport]
    [Guid("0000000f-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMoniker
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.Interface)] object? stream);
        void Save([MarshalAs(UnmanagedType.Interface)] object? stream, bool clearDirty);
        void GetSizeMax(out long size);
        void BindToObject([MarshalAs(UnmanagedType.Interface)] object? bindCtx,
            [MarshalAs(UnmanagedType.Interface)] object? toLeft, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
        void BindToStorage([MarshalAs(UnmanagedType.Interface)] object? bindCtx,
            [MarshalAs(UnmanagedType.Interface)] object? toLeft, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.Struct)] ref object value, IntPtr errorLog);

        [PreserveSig]
        int Write([MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
