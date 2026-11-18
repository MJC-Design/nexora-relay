using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Desktop;

public partial class RemoteViewerWindow : Window
{
    private readonly ClientSession _session;
    private readonly HashSet<int> _pressedKeys = new();
    private readonly object _frameGate = new();

    private byte[]? _pendingFrame;
    private bool _renderQueued;
    private int _frameWidth;
    private int _frameHeight;
    private int _cursorX;
    private int _cursorY;
    private long _lastMouseMoveSent;
    private bool _fullscreen;
    private bool _finished;
    private bool _audioLocal;
    private bool _invitePending;
    private int _unread;
    private VideoCallWindow? _videoWindow;
    private volatile ScreenRecorder? _recorder;

    private static readonly IBrush CallActiveBrush = Ui.BrushFrom("#059669");
    private static readonly IBrush RecordActiveBrush = Ui.BrushFrom("#E5484D");

    public RemoteViewerWindow(ClientSession session)
    {
        InitializeComponent();
        _session = session;
        Ui.ApplyIcon(this);

        Title = Localization.Format("Viewer_Title", session.HostName);
        TitleText.Text = Localization.Format("Viewer_Title", session.HostName);
        InfoText.Text = Localization.Format("Viewer_Info",
            session.HostName, session.ScreenWidth, session.ScreenHeight);

        ChatArea.Glass = true;
        ChatOverlaySubtitle.Text = Localization.Format("Viewer_ChatWith", session.HostName);

        _session.FrameReceived += OnFrameReceived;
        _session.CursorChanged += OnCursorChanged;
        _session.Disconnected += OnDisconnected;
        _session.LogMessage += OnSessionLog;
        _session.TransferProgress += OnTransferProgress;
        _session.TransferCompleted += OnTransferCompleted;
        _session.OutgoingProgress += OnOutgoingProgress;
        _session.PullRequestResult += OnPullRequestResult;
        _session.ChatReceived += OnChatReceived;
        _session.AudioStateChanged += OnAudioStateChanged;
        _session.AudioError += OnAudioError;
        _session.VideoInvited += OnVideoInvited;
        _session.VideoAccepted += OnVideoAccepted;
        _session.VideoRejected += OnVideoRejected;
        _session.SaveFileRequested = SaveIncomingFileAsync;

        ChatArea.MessageSent += text => _session.SendChat(text);

        Loaded += (_, _) =>
        {
            Ui.TrimWindowToScreen(this);
            ScreenHost.Focus();
        };
        Deactivated += (_, _) => ReleaseKeys();
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

    private void OnFrameReceived(byte[] jpeg)
    {
        _recorder?.AddFrame(jpeg);

        lock (_frameGate)
        {
            _pendingFrame = jpeg;
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.UIThread.Post(RenderFrame, DispatcherPriority.Render);
    }

    private void RenderFrame()
    {
        byte[]? data;
        lock (_frameGate)
        {
            data = _pendingFrame;
            _pendingFrame = null;
            _renderQueued = false;
        }

        if (data is null || _finished) return;

        try
        {
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            _frameWidth = bitmap.PixelSize.Width;
            _frameHeight = bitmap.PixelSize.Height;

            var previous = ScreenImage.Source as Bitmap;
            ScreenImage.Source = bitmap;
            previous?.Dispose();

            WaitingBox.IsVisible = false;
            UpdateCursorVisual();
        }
        catch
        {
            // Un fotograma dañado se descarta.
        }
    }

    private void OnCursorChanged(int x, int y)
    {
        _cursorX = x;
        _cursorY = y;
        Dispatcher.UIThread.Post(UpdateCursorVisual);
    }

    private void UpdateCursorVisual()
    {
        if (_frameWidth == 0 || _frameHeight == 0 ||
            _session.ScreenWidth == 0 || _session.ScreenHeight == 0)
            return;

        var (ox, oy, w, h) = GetDisplayedRect();
        if (w <= 0 || h <= 0) return;

        var px = ox + _cursorX * w / _session.ScreenWidth;
        var py = oy + _cursorY * h / _session.ScreenHeight;

        CursorDot.IsVisible = true;
        Canvas.SetLeft(CursorDot, px - CursorDot.Width / 2);
        Canvas.SetTop(CursorDot, py - CursorDot.Height / 2);
    }

    private (double Ox, double Oy, double W, double H) GetDisplayedRect()
    {
        var cw = ScreenImage.Bounds.Width;
        var ch = ScreenImage.Bounds.Height;
        if (cw <= 0 || ch <= 0 || _frameWidth == 0 || _frameHeight == 0)
            return (0, 0, 0, 0);

        var scale = Math.Min(cw / _frameWidth, ch / _frameHeight);
        var w = _frameWidth * scale;
        var h = _frameHeight * scale;
        return ((cw - w) / 2, (ch - h) / 2, w, h);
    }

    private bool TryMap(Point point, out int x, out int y)
    {
        x = y = 0;
        var (ox, oy, w, h) = GetDisplayedRect();
        if (w <= 0 || h <= 0) return false;

        var lx = point.X - ox;
        var ly = point.Y - oy;
        if (lx < 0 || ly < 0 || lx > w || ly > h) return false;

        var hostWidth = Math.Max(1, _session.ScreenWidth);
        var hostHeight = Math.Max(1, _session.ScreenHeight);
        x = (int)Math.Round(Math.Clamp(lx / w, 0, 1) * (hostWidth - 1));
        y = (int)Math.Round(Math.Clamp(ly / h, 0, 1) * (hostHeight - 1));
        return true;
    }

    private void ScreenHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        var now = Environment.TickCount64;
        if (now - _lastMouseMoveSent < 25) return;
        _lastMouseMoveSent = now;
        _session.SendInputMove(x, y);
    }

    private void ScreenHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ScreenHost.Focus();
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        var button = e.GetCurrentPoint(ScreenHost).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonPressed => 1,
            PointerUpdateKind.MiddleButtonPressed => 2,
            PointerUpdateKind.LeftButtonPressed => 0,
            _ => -1,
        };
        if (button < 0) return;

        _session.SendInputButton(button, true, x, y);
        e.Handled = true;
    }

    private void ScreenHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        var button = e.GetCurrentPoint(ScreenHost).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonReleased => 1,
            PointerUpdateKind.MiddleButtonReleased => 2,
            PointerUpdateKind.LeftButtonReleased => 0,
            _ => -1,
        };
        if (button < 0) return;

        _session.SendInputButton(button, false, x, y);
        e.Handled = true;
    }

    private void ScreenHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;
        _session.SendInputWheel((int)Math.Round(e.Delta.Y * 120), x, y);
        e.Handled = true;
    }

    private void ScreenHost_KeyDown(object? sender, KeyEventArgs e)
    {
        var vk = VirtualKey(e);
        if (vk == 0) return;

        _pressedKeys.Add(vk);
        _session.SendInputKey(vk, true);
        e.Handled = true;
    }

    private void ScreenHost_KeyUp(object? sender, KeyEventArgs e)
    {
        var vk = VirtualKey(e);
        if (vk == 0) return;

        _pressedKeys.Remove(vk);
        _session.SendInputKey(vk, false);
        e.Handled = true;
    }

    private void ReleaseKeys()
    {
        if (_pressedKeys.Count == 0) return;
        foreach (var vk in _pressedKeys)
            _session.SendInputKey(vk, false);
        _pressedKeys.Clear();
    }

    private void ToggleChat_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var show = !ChatPane.IsVisible;
        ChatPane.IsVisible = show;
        if (!show)
        {
            ScreenHost.Focus();
            return;
        }

        _unread = 0;
        ChatButton.Content = Localization.Get("Viewer_Chat");
        ChatArea.FocusInput();
        Dispatcher.UIThread.Post(ChatArea.ScrollToEnd, DispatcherPriority.Background);
    }

    private void Audio_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
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
        TransferText.Text = Localization.Get("Video_InviteSent");
    }

    private void OnVideoInvited()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_finished) return;

            var accepted = await Dialogs.ConfirmAsync(this,
                Localization.Get("Video_InviteTitle"),
                Localization.Format("Video_InviteText", _session.HostName),
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

    private async void Record_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_recorder is not null)
        {
            await StopRecordingAsync(showDialog: true);
            return;
        }

        var suggested = $"Nexora-{DateTime.Now:yyyyMMdd-HHmm}.avi";
        var path = await Dialogs.PickSaveFileAsync(this, Localization.Get("Dlg_RecordSaveAs"), suggested);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            _recorder = ScreenRecorder.Start(path);
            RecordButton.Content = Localization.Get("Viewer_StopRecord");
            RecordButton.Background = RecordActiveBrush;
            RecordButton.Foreground = Brushes.White;
            RecBadge.IsVisible = true;
            TransferText.Text = Localization.Get("Log_RecordingStarted");
        }
        catch (Exception ex)
        {
            _recorder = null;
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_RecordingTitle"),
                Localization.Format("Dlg_RecordingFailed", EngineMessages.Translate(ex.Message)));
        }
    }

    private async Task StopRecordingAsync(bool showDialog)
    {
        var recorder = _recorder;
        if (recorder is null) return;

        _recorder = null;
        RecordButton.Content = Localization.Get("Viewer_Record");
        RecordButton.ClearValue(BackgroundProperty);
        RecordButton.ClearValue(ForegroundProperty);
        RecBadge.IsVisible = false;

        var frames = await recorder.StopAsync();
        await recorder.DisposeAsync();

        if (!showDialog) return;

        if (frames > 0)
        {
            TransferText.Text = Localization.Format("Log_RecordingSaved", recorder.Path);
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_RecordingTitle"),
                Localization.Format("Dlg_RecordingSaved", recorder.Path));
        }
        else
        {
            TransferText.Text = Localization.Get("Dlg_RecordingEmpty");
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_RecordingTitle"),
                Localization.Get("Dlg_RecordingEmpty"));
        }
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

    private void OnAudioStateChanged(bool local, bool remote)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _audioLocal = local;
            if (local)
            {
                AudioButton.Content = Localization.Get("Viewer_EndAudio");
                AudioButton.Background = CallActiveBrush;
                AudioButton.Foreground = Brushes.White;
                AudioStatusText.Text = Localization.Get("Viewer_AudioActive");
            }
            else
            {
                AudioButton.Content = Localization.Get("Viewer_Audio");
                AudioButton.ClearValue(BackgroundProperty);
                AudioButton.ClearValue(ForegroundProperty);
                AudioStatusText.Text = remote ? Localization.Get("Viewer_AudioRemote") : "";
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

    private void FullScreen_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _fullscreen = !_fullscreen;
        WindowState = _fullscreen ? WindowState.FullScreen : WindowState.Normal;
        FullscreenButton.Content = _fullscreen
            ? Localization.Get("Viewer_ExitFullscreen")
            : Localization.Get("Viewer_Fullscreen");
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
            TransferBar.Value = 0;
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, Localization.Get("Transfer_Header"),
                EngineMessages.Translate(ex.Message));
        }
    }

    private async void PullFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            TransferText.Text = Localization.Get("Viewer_Requesting");
            var accepted = await _session.RequestFileFromHostAsync();
            TransferText.Text = accepted
                ? Localization.Get("Viewer_WaitingFile")
                : Localization.Get("Viewer_CancelledByHost");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, Localization.Get("Transfer_Header"),
                EngineMessages.Translate(ex.Message));
        }
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

    private void OnDisconnected(string reason)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_finished) return;
            _finished = true;
            _videoWindow?.Close();
            _videoWindow = null;
            await StopRecordingAsync(showDialog: false);
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_SessionTitle"),
                EngineMessages.Translate(reason));
            Close();
        });
    }

    private async void Disconnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _finished = true;
        ReleaseKeys();
        _videoWindow?.Close();
        _videoWindow = null;
        await StopRecordingAsync(showDialog: false);
        try { await _session.DisconnectAsync(); } catch { }
        Close();
    }

    private async void OnClosedInternal(object? sender, EventArgs e)
    {
        _finished = true;
        ReleaseKeys();
        _videoWindow?.Close();
        _videoWindow = null;
        await StopRecordingAsync(showDialog: false);
        try { _session.StopAudioCall(); } catch { }
        await _session.DisposeAsync();
    }

    private static int VirtualKey(KeyEventArgs e)
    {
        var vk = FromPhysical(e.PhysicalKey);
        return vk != 0 ? vk : FromKey(e.Key);
    }

    private static int FromPhysical(PhysicalKey key)
    {
        var code = (int)key;
        if (code >= (int)PhysicalKey.A && code <= (int)PhysicalKey.Z)
            return 0x41 + code - (int)PhysicalKey.A;
        if (code >= (int)PhysicalKey.Digit0 && code <= (int)PhysicalKey.Digit9)
            return 0x30 + code - (int)PhysicalKey.Digit0;
        if (code >= (int)PhysicalKey.F1 && code <= (int)PhysicalKey.F24)
            return 0x70 + code - (int)PhysicalKey.F1;
        if (code >= (int)PhysicalKey.NumPad0 && code <= (int)PhysicalKey.NumPad9)
            return 0x60 + code - (int)PhysicalKey.NumPad0;

        return key switch
        {
            PhysicalKey.Backquote => 0xC0,
            PhysicalKey.Minus => 0xBD,
            PhysicalKey.Equal => 0xBB,
            PhysicalKey.BracketLeft => 0xDB,
            PhysicalKey.BracketRight => 0xDD,
            PhysicalKey.Backslash => 0xDC,
            PhysicalKey.Semicolon => 0xBA,
            PhysicalKey.Quote => 0xDE,
            PhysicalKey.Comma => 0xBC,
            PhysicalKey.Period => 0xBE,
            PhysicalKey.Slash => 0xBF,
            PhysicalKey.IntlBackslash => 0xE2,
            PhysicalKey.Backspace => 0x08,
            PhysicalKey.Tab => 0x09,
            PhysicalKey.Enter => 0x0D,
            PhysicalKey.NumPadEnter => 0x0D,
            PhysicalKey.Escape => 0x1B,
            PhysicalKey.Space => 0x20,
            PhysicalKey.CapsLock => 0x14,
            PhysicalKey.ShiftLeft => 0xA0,
            PhysicalKey.ShiftRight => 0xA1,
            PhysicalKey.ControlLeft => 0xA2,
            PhysicalKey.ControlRight => 0xA3,
            PhysicalKey.AltLeft => 0xA4,
            PhysicalKey.AltRight => 0xA5,
            PhysicalKey.MetaLeft => 0x5B,
            PhysicalKey.MetaRight => 0x5C,
            PhysicalKey.ContextMenu => 0x5D,
            PhysicalKey.ArrowLeft => 0x25,
            PhysicalKey.ArrowUp => 0x26,
            PhysicalKey.ArrowRight => 0x27,
            PhysicalKey.ArrowDown => 0x28,
            PhysicalKey.Home => 0x24,
            PhysicalKey.End => 0x23,
            PhysicalKey.PageUp => 0x21,
            PhysicalKey.PageDown => 0x22,
            PhysicalKey.Insert => 0x2D,
            PhysicalKey.Delete => 0x2E,
            PhysicalKey.NumPadAdd => 0x6B,
            PhysicalKey.NumPadSubtract => 0x6D,
            PhysicalKey.NumPadMultiply => 0x6A,
            PhysicalKey.NumPadDivide => 0x6F,
            PhysicalKey.NumPadDecimal => 0x6E,
            PhysicalKey.NumLock => 0x90,
            PhysicalKey.ScrollLock => 0x91,
            PhysicalKey.Pause => 0x13,
            PhysicalKey.PrintScreen => 0x2C,
            PhysicalKey.BrowserBack => 0xA6,
            PhysicalKey.BrowserForward => 0xA7,
            PhysicalKey.BrowserRefresh => 0xA8,
            PhysicalKey.BrowserStop => 0xA9,
            PhysicalKey.BrowserSearch => 0xAA,
            PhysicalKey.BrowserFavorites => 0xAB,
            PhysicalKey.BrowserHome => 0xAC,
            PhysicalKey.AudioVolumeMute => 0xAD,
            PhysicalKey.AudioVolumeDown => 0xAE,
            PhysicalKey.AudioVolumeUp => 0xAF,
            PhysicalKey.MediaTrackNext => 0xB0,
            PhysicalKey.MediaTrackPrevious => 0xB1,
            PhysicalKey.MediaStop => 0xB2,
            PhysicalKey.MediaPlayPause => 0xB3,
            PhysicalKey.LaunchMail => 0xB4,
            PhysicalKey.LaunchApp1 => 0xB6,
            PhysicalKey.LaunchApp2 => 0xB7,
            _ => 0,
        };
    }

    private static int FromKey(Key key)
    {
        var code = (int)key;
        if (code >= (int)Key.A && code <= (int)Key.Z)
            return 0x41 + code - (int)Key.A;
        if (code >= (int)Key.D0 && code <= (int)Key.D9)
            return 0x30 + code - (int)Key.D0;
        if (code >= (int)Key.NumPad0 && code <= (int)Key.NumPad9)
            return 0x60 + code - (int)Key.NumPad0;

        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.LeftShift => 0xA0,
            Key.RightShift => 0xA1,
            Key.LeftCtrl => 0xA2,
            Key.RightCtrl => 0xA3,
            Key.LeftAlt => 0xA4,
            Key.RightAlt => 0xA5,
            _ => 0,
        };
    }
}
