using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Desktop;

public partial class FileTransferWindow : Window
{
    private static readonly IBrush CallActiveBrush = Ui.BrushFrom("#059669");
    private readonly ClientSession _session;
    private bool _finished;
    private bool _audioLocal;

    public FileTransferWindow(ClientSession session)
    {
        InitializeComponent();
        _session = session;
        Ui.ApplyIcon(this);

        Title = Localization.Format("Transfer_Title", session.HostName);
        TitleText.Text = Localization.Get("Transfer_Header");
        PeerText.Text = Localization.Format("Transfer_Peer", session.HostName);

        _session.LogMessage += OnSessionLog;
        _session.TransferProgress += OnTransferProgress;
        _session.TransferCompleted += OnTransferCompleted;
        _session.OutgoingProgress += OnOutgoingProgress;
        _session.PullRequestResult += OnPullRequestResult;
        _session.ChatReceived += OnChatReceived;
        _session.AudioStateChanged += OnAudioStateChanged;
        _session.AudioError += OnAudioError;
        _session.Disconnected += OnDisconnected;
        _session.SaveFileRequested = SaveIncomingFileAsync;

        ChatArea.MessageSent += text => _session.SendChat(text);
        Closed += OnClosedInternal;
    }

    private Task<string?> SaveIncomingFileAsync(FileOffer offer)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_finished)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var accepted = await Dialogs.ConfirmAsync(this,
                    Localization.Get("Dlg_IncomingFile"),
                    Localization.Format("Dlg_FilePrompt", offer.FileName, Ui.FormatSize(offer.FileSize)),
                    Localization.Get("Dlg_SaveReceived"),
                    Localization.Get("Dlg_Cancel"));
                if (!accepted)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var path = await Dialogs.PickSaveFileAsync(this,
                    Localization.Get("Dlg_SaveReceived"), offer.FileName);
                tcs.TrySetResult(path);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    private async void SendFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await Dialogs.PickOpenFileAsync(this, Localization.Get("Dlg_PickSend"));
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            SendFileButton.IsEnabled = false;
            TransferText.Text = Localization.Get("Dlg_SendingFile");
            TransferBar.Value = 0;
            await _session.SendFileAsync(path);
            TransferText.Text = Localization.Get("Viewer_FileSent");
            TransferBar.Value = 0;
        }
        catch (Exception ex)
        {
            var message = EngineMessages.Translate(ex.Message);
            TransferText.Text = message;
            await Dialogs.InfoAsync(this, Localization.Get("Transfer_Header"), message);
        }
        finally
        {
            SendFileButton.IsEnabled = true;
        }
    }

    private async void PullFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            PullFileButton.IsEnabled = false;
            TransferText.Text = Localization.Get("Viewer_Requesting");
            var accepted = await _session.RequestFileFromHostAsync();
            TransferText.Text = accepted
                ? Localization.Get("Viewer_WaitingFile")
                : Localization.Get("Viewer_CancelledByOther");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, Localization.Get("Transfer_Header"),
                EngineMessages.Translate(ex.Message));
        }
        finally
        {
            PullFileButton.IsEnabled = true;
        }
    }

    private void AudioCall_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
    }

    private void OnTransferProgress(int fileId, long received, long total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var maximum = Math.Max(1, total);
            TransferBar.Maximum = maximum;
            TransferBar.Value = Math.Min(received, maximum);
            TransferText.Text = Localization.Format("Viewer_Receiving", received * 100 / maximum);
        });
    }

    private void OnTransferCompleted(int fileId, bool ok, string? error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TransferBar.Value = 0;
            TransferText.Text = ok
                ? Localization.Get("Viewer_FileReceived")
                : Localization.Format("Viewer_FileIncomplete", error ?? "");
        });
    }

    private void OnOutgoingProgress(long sent, long total)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var maximum = Math.Max(1, total);
            TransferBar.Maximum = maximum;
            TransferBar.Value = Math.Min(sent, maximum);
            TransferText.Text = Localization.Format("Viewer_Sending", sent * 100 / maximum);
        });
    }

    private void OnPullRequestResult(bool ok, string? message)
    {
        if (ok || string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() => TransferText.Text = EngineMessages.Translate(message));
    }

    private void OnSessionLog(string message)
    {
        Dispatcher.UIThread.Post(() => TransferText.Text = EngineMessages.Translate(message));
    }

    private void OnChatReceived(string text, string? from)
    {
        Dispatcher.UIThread.Post(() => ChatArea.AddIncoming(from, text));
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
                AudioStatusText.Text = remote ? Localization.Get("Transfer_AudioRemote") : "";
            }
        });
    }

    private void OnAudioError(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var text = EngineMessages.Translate(message);
            AudioStatusText.Text = text;
            if (!_finished)
                await Dialogs.InfoAsync(this, Localization.Get("Viewer_Audio"), text);
        });
    }

    private void OnDisconnected(string reason)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_finished) return;
            _finished = true;
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_SessionTitle"),
                EngineMessages.Translate(reason));
            Close();
        });
    }

    private async void End_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _finished = true;
        try { _session.StopAudioCall(); } catch { }
        try { await _session.DisconnectAsync(); } catch { }
        Close();
    }

    private async void OnClosedInternal(object? sender, EventArgs e)
    {
        _finished = true;
        try { _session.StopAudioCall(); } catch { }
        await _session.DisposeAsync();
    }
}
