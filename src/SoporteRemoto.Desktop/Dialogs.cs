using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SoporteRemoto.Desktop;

public static class Dialogs
{
    public static Task InfoAsync(Window owner, string title, string message, string? primaryText = null)
    {
        var dialog = new MessageDialog(title, message,
            primaryText ?? Localization.Get("Dlg_OK"), null, false);
        return dialog.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message,
        string? primaryText = null, string? secondaryText = null, bool danger = false)
    {
        var dialog = new MessageDialog(title, message,
            primaryText ?? Localization.Get("Dlg_Accept"),
            secondaryText ?? Localization.Get("Dlg_Cancel"), danger);
        return await dialog.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PickOpenFileAsync(Window owner, string title)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickSaveFileAsync(Window owner, string title, string suggestedName)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }
}
