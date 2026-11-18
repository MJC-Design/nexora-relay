using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SoporteRemoto.Desktop;

public static class Localization
{
    public sealed record Language(string Code, string Name);

    public static readonly Language[] Languages =
    {
        new("es", "Español"),
        new("en", "English"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("pt", "Português"),
    };

    private static ResourceDictionary? _dictionary;
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("es");

    public static string Current { get; private set; } = "es";

    public static event Action? Changed;

    public static string Normalize(string? code)
    {
        var two = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (two.Length > 2) two = two[..2];
        foreach (var language in Languages)
            if (language.Code == two) return two;

        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        foreach (var language in Languages)
            if (language.Code == system) return system;

        return "en";
    }

    public static void Apply(string? code)
    {
        var target = Normalize(code);
        if (target == Current && _dictionary is not null) return;

        var uri = new Uri($"avares://Nexora/Assets/Lang/{target}.axaml");
        var loaded = (ResourceDictionary)AvaloniaXamlLoader.Load(uri);
        var app = Application.Current ?? throw new InvalidOperationException("Application is not ready.");

        if (_dictionary is not null)
            app.Resources.MergedDictionaries.Remove(_dictionary);

        app.Resources.MergedDictionaries.Add(loaded);
        _dictionary = loaded;
        Current = target;
        _culture = CultureInfo.GetCultureInfo(target);
        CultureInfo.CurrentUICulture = _culture;

        Changed?.Invoke();
    }

    public static string Get(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string text
            ? text
            : key;

    public static string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    public static CultureInfo Culture => _culture;
}

public static class Ui
{
    public static void ApplyIcon(Window window)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Nexora/Assets/logo-mark-128.png"));
            window.Icon = new WindowIcon(new Bitmap(stream));
        }
        catch
        {
        }
    }

    public static IBrush BrushFrom(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : string.Format(Localization.Culture, "{0:0.#} {1}", value, units[unit]);
    }

    public static void TrimWindowToScreen(Window window)
    {
        try
        {
            var screen = window.Screens.Primary;
            var scale = screen?.Scaling ?? 0;
            var area = screen?.WorkingArea ?? default;
            var maxWidth = Math.Max(640, area.Width / (scale <= 0 ? 1 : scale) - 30);
            var maxHeight = Math.Max(460, area.Height / (scale <= 0 ? 1 : scale) - 84);

            if (window.MinWidth > maxWidth) window.MinWidth = maxWidth;
            if (window.MinHeight > maxHeight) window.MinHeight = maxHeight;
            if (window.Width > maxWidth) window.Width = maxWidth;
            if (window.Height > maxHeight) window.Height = maxHeight;
        }
        catch
        {
        }
    }
}
