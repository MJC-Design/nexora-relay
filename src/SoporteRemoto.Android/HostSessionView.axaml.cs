using Avalonia.Controls;
using Avalonia.Threading;
using SoporteRemoto.Android.Services;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Android;

public partial class HostSessionView : UserControl
{
    private readonly HostSession _session;
    private bool _closed;
    private int _unread;

    public event EventHandler? Closed;

    public HostSessionView(HostSession session, string description)
    {
        InitializeComponent();
        _session = session;

        TitleText.Text = description;
        InfoText.Text = $"{session.ClientName} · {session.RemoteAddress}";
        ModeText.Text = session.Mode == SessionMode.Files
            ? Localization.Get("Host_TitleFiles")
            : Localization.Get("Android_ShareSubtitle");
        StatusText.Text = Localization.Format("Eng_SessionStarted", session.ClientName);

        _session.ChatReceived += OnChatReceived;
        _session.SessionEnded += OnSessionEnded;
        ChatArea.MessageSent += text => _ = _session.SendChatAsync(text);

        Loaded += (_, _) => RefreshState();
    }

    private void RefreshState()
    {
        var sharing = AndroidScreenSharing.IsReady;
        ShareStatusText.Text = sharing
            ? Localization.Get("Android_SharingNow")
            : Localization.Get("Android_SharingStopped");
        ShareButton.IsVisible = !sharing;
        ShareButton.Content = Localization.Get("Android_ShareNow");

        var activity = MainActivity.Current;
        var enabled = activity is not null && AndroidRemoteInput.IsEnabled(activity);
        AccessButton.Content = enabled
            ? Localization.Get("Android_AccessOn")
            : Localization.Get("Android_AccessOff");
    }

    private async void Share_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Current;
        if (activity is null) return;

        var started = await ScreenShare.StartAsync(activity);
        if (!started)
        {
            await AndroidDialogs.InfoAsync(activity, Localization.Get("Dlg_Error"),
                Localization.Get("Android_ScreenPermissionDenied"));
        }
        RefreshState();
    }

    private void Access_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Current;
        if (activity is not null) AndroidSettingsHelper.OpenAccessibility(activity);
        RefreshState();
    }

    private void OnChatReceived(string text, string? from)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ChatArea.AddIncoming(from, text);
            if (ChatPane.IsVisible) return;

            _unread++;
            ChatButton.Content = Localization.Format("Viewer_ChatUnread", _unread);
        });
    }

    private void OnSessionEnded(string reason)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_closed) return;
            _closed = true;

            var activity = MainActivity.Current;
            if (activity is not null)
                await AndroidDialogs.InfoAsync(activity, Localization.Get("Dlg_SessionTitle"),
                    EngineMessages.Translate(reason));

            Closed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Chat_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var show = !ChatPane.IsVisible;
        ChatPane.IsVisible = show;
        if (!show) return;

        _unread = 0;
        ChatButton.Content = Localization.Get("Viewer_Chat");
        ChatArea.FocusInput();
        Dispatcher.UIThread.Post(ChatArea.ScrollToEnd, DispatcherPriority.Background);
    }

    private async void End_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_closed) return;

        var activity = MainActivity.Current;
        if (activity is not null)
        {
            var confirm = await AndroidDialogs.ConfirmAsync(activity,
                Localization.Get("Dlg_SessionTitle"),
                Localization.Get("Host_EndSession"),
                Localization.Get("Dlg_OK"),
                Localization.Get("Dlg_Cancel"));
            if (!confirm) return;
        }

        _closed = true;
        try { await _session.EndSessionAsync(); } catch { }
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
