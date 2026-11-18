using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Desktop;

public partial class HostSessionWindow : Window
{
    private static readonly IBrush CallActiveBrush = Ui.BrushFrom("#059669");
    private readonly HostSession _session;
    private bool _ended;
    private bool _audioLocal;
    private bool _invitePending;
    private VideoCallWindow? _videoWindow;

    public HostSessionWindow(HostSession session, string description)
    {
        InitializeComponent();
        _session = session;
        Ui.ApplyIcon(this);

        PeerText.Text = description;

        if (session.Mode == SessionMode.Files)
        {
            Title = Localization.Get("Host_TitleFiles");
            SessionTitleText.Text = Localization.Get("Host_TitleFiles");
            SessionSubtitleText.Text = Localization.Get("Host_SubtitleFiles");
            TransferText.Text = Localization.Get("Transfer_SessionStatus");
            SessionNoteText.Text = Localization.Get("Transfer_NoScreenText");
        }
        else
        {
            Title = Localization.Get("Host_TitleScreen");
        }

        ChatArea.MessageSent += text => _ = _session.SendChatAsync(text);
        _session.ChatReceived += OnChatReceived;
        _session.AudioStateChanged += OnAudioStateChanged;
        _session.AudioError += OnAudioError;
        _session.VideoInvited += OnVideoInvited;
        _session.VideoAccepted += OnVideoAccepted;
        _session.VideoRejected += OnVideoRejected;

        Closed += OnClosedInternal;
    }

    private async void OnClosedInternal(object? sender, EventArgs e)
    {
        if (_ended) return;
        _ended = true;
        _videoWindow?.Close();
        await _session.EndAsync();
    }

    private void VideoCall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_videoWindow is { IsLoaded: true })
        {
            _videoWindow.Activate();
            return;
        }

        _invitePending = true;
        _session.SendVideoInvite();
        AudioStatusText.Text = Localization.Get("Video_InviteSent");
    }

    private void OnVideoInvited()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_ended) return;

            var accepted = await Dialogs.ConfirmAsync(this,
                Localization.Get("Video_InviteTitle"),
                Localization.Format("Video_InviteText", _session.ClientName),
                Localization.Get("Dlg_Accept"),
                Localization.Get("Dlg_Cancel"));
            _session.RespondVideoInvite(accepted);
            if (accepted) OpenVideoCall();
        });
    }

    private void OnVideoAccepted()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_invitePending) return;
            _invitePending = false;
            OpenVideoCall();
        });
    }

    private void OnVideoRejected()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!_invitePending) return;
            _invitePending = false;
            await Dialogs.InfoAsync(this, Localization.Get("Video_InviteTitle"),
                Localization.Get("Video_Rejected"));
        });
    }

    private void OpenVideoCall()
    {
        if (_videoWindow is { IsLoaded: true })
        {
            _videoWindow.Activate();
            return;
        }

        _invitePending = false;
        _videoWindow = new VideoCallWindow(_session, autoStart: false);
        _videoWindow.Closed += (_, _) => _videoWindow = null;
        _videoWindow.Show(this);
        _videoWindow.BeginCall();
    }

    private void OnChatReceived(string text, string? from)
    {
        Dispatcher.UIThread.Post(() => ChatArea.AddIncoming(from, text));
    }

    private void AudioCall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
    }

    private void OnAudioStateChanged(bool local, bool remote)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _audioLocal = local;
            if (local)
            {
                AudioCallButton.Content = Localization.Get("Host_EndAudio");
                AudioCallButton.Background = CallActiveBrush;
                AudioCallButton.Foreground = Brushes.White;
                AudioStatusText.Text = Localization.Get("Host_AudioActive");
            }
            else
            {
                AudioCallButton.Content = Localization.Get("Host_StartAudio");
                AudioCallButton.ClearValue(BackgroundProperty);
                AudioCallButton.ClearValue(ForegroundProperty);
                AudioStatusText.Text = remote ? Localization.Get("Host_AudioRemote") : "";
            }
        });
    }

    private void OnAudioError(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var text = EngineMessages.Translate(message);
            AudioStatusText.Text = text;
            if (!_ended)
                await Dialogs.InfoAsync(this, Localization.Get("Viewer_Audio"), text);
        });
    }

    private async void SendFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await Dialogs.PickOpenFileAsync(this, Localization.Get("Dlg_PickSend"));
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            TransferText.Text = Localization.Get("Dlg_SendingFile");
            TransferBar.Value = 0;
            await _session.SendFileAsync(path);
            TransferText.Text = Localization.Get("Viewer_FileSent");
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_FileSentTitle"),
                Localization.Get("Dlg_FileSentText"));
        }
        catch (Exception ex)
        {
            var message = EngineMessages.Translate(ex.Message);
            TransferText.Text = message;
            await Dialogs.InfoAsync(this, Localization.Get("Transfer_Header"), message);
        }
    }

    private async void End_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ended = true;
        try { _session.StopAudioCall(); } catch { }
        await _session.EndAsync();
        Close();
    }
}
