using Android.App;
using Android.Content;
using Android.Database;
using Android.OS;
using Android.Provider;

namespace SoporteRemoto.Android.Services;

public static class AndroidFiles
{
    public const int PickRequestCode = 4252;

    private static TaskCompletionSource<string?>? _pending;

    public static Task<string?> PickFileAsync(Activity activity)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        try
        {
            var intent = new Intent(Intent.ActionOpenDocument)
                .AddCategory(Intent.CategoryOpenable)
                .SetType("*/*");
            activity.StartActivityForResult(intent, PickRequestCode);
        }
        catch
        {
            tcs.TrySetResult(null);
        }

        return tcs.Task;
    }

    internal static void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != PickRequestCode) return;

        var pending = _pending;
        _pending = null;
        if (pending is null) return;

        if (resultCode != Result.Ok || data?.Data is null)
        {
            pending.TrySetResult(null);
            return;
        }

        try
        {
            pending.TrySetResult(CopyToCache(data.Data));
        }
        catch
        {
            pending.TrySetResult(null);
        }
    }

    private static string CopyToCache(global::Android.Net.Uri uri)
    {
        var context = MainActivity.Current
                      ?? throw new InvalidOperationException("La actividad no está disponible.");

        var name = QueryName(context, uri) ?? uri.LastPathSegment ?? "archivo";
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)) name = "archivo";

        var folder = Path.Combine(context.CacheDir!.AbsolutePath, "enviados");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);

        using var input = context.ContentResolver?.OpenInputStream(uri)
                          ?? throw new InvalidOperationException("No se pudo abrir el archivo.");
        using var output = File.Create(path);
        input.CopyTo(output);
        return path;
    }

    private static string? QueryName(Context context, global::Android.Net.Uri uri)
    {
        try
        {
            using ICursor? cursor = context.ContentResolver?.Query(
                uri, new[] { OpenableColumns.DisplayName }, null, null, null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (index >= 0) return cursor.GetString(index);
            }
        }
        catch
        {
            // Se usa el nombre del segmento de la URI.
        }

        return null;
    }

    public static string IncomingFolder(Context context)
    {
        var root = context.GetExternalFilesDir(global::Android.OS.Environment.DirectoryDownloads)
                   ?? context.FilesDir;
        var folder = Path.Combine(root!.AbsolutePath, "Nexora");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string IncomingPath(Context context, string fileName)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe)) safe = "archivo";
        return Path.Combine(IncomingFolder(context), safe);
    }
}
