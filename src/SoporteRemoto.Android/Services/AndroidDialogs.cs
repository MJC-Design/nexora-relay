using Android.App;
using Android.Content;

namespace SoporteRemoto.Android.Services;

public static class AndroidDialogs
{
    public static Task InfoAsync(Activity activity, string title, string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            try
            {
                new AlertDialog.Builder(activity)
                    .SetTitle(title)
                    .SetMessage(text)
                    .SetPositiveButton("OK", new EventHandler<DialogClickEventArgs>((_, _) => tcs.TrySetResult(true)))
                    .SetOnCancelListener(new CancelListener(tcs))
                    .Show();
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    public static Task<bool> ConfirmAsync(Activity activity, string title, string text, string ok, string cancel)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.RunOnUiThread(() =>
        {
            try
            {
                new AlertDialog.Builder(activity)
                    .SetTitle(title)
                    .SetMessage(text)
                    .SetPositiveButton(ok, new EventHandler<DialogClickEventArgs>((_, _) => tcs.TrySetResult(true)))
                    .SetNegativeButton(cancel, new EventHandler<DialogClickEventArgs>((_, _) => tcs.TrySetResult(false)))
                    .SetOnCancelListener(new CancelListener(tcs))
                    .Show();
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private sealed class CancelListener : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        private readonly TaskCompletionSource<bool> _tcs;
        public CancelListener(TaskCompletionSource<bool> tcs) => _tcs = tcs;
        public void OnCancel(IDialogInterface? dialog) => _tcs.TrySetResult(false);
    }
}
