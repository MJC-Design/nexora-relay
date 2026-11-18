using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Desktop;

public partial class VideoCallWindow : Window
{
    private static readonly IBrush CallActiveBrush = Ui.BrushFrom("#059669");

    private const double PipMinWidth = 140;
    private const double PipDefaultWidth = 218;
    private const double PipDefaultHeight = 152;

    private readonly IVideoSession _session;
    private readonly bool _autoStart;
    private readonly object _remoteGate = new();
    private readonly object _localGate = new();
    private readonly List<MediaDeviceInfo> _cameraDevices = new();
    private readonly List<MediaDeviceInfo> _microphoneDevices = new();

    private byte[]? _pendingRemote;
    private byte[]? _pendingLocal;
    private bool _remoteRenderQueued;
    private bool _localRenderQueued;
    private bool _audioLocal;
    private bool _callStarted;
    private bool _ending;
    private bool _closed;
    private int _unread;
    private bool _loadingDevices;
    private MediaDeviceInfo? _activeCamera;
    private MediaDeviceInfo? _activeMicrophone;
    private bool _pipPlaced;
    private bool _pipDragging;
    private bool _pipResizing;
    private Point _pipGrabPoint;
    private double _pipStartLeft;
    private double _pipStartTop;
    private double _pipStartWidth = PipDefaultWidth;
    private double _pipStartHeight = PipDefaultHeight;

    public VideoCallWindow(IVideoSession session, bool autoStart)
    {
        InitializeComponent();
        _session = session;
        _autoStart = autoStart;
        Ui.ApplyIcon(this);

        Title = Localization.Format("Video_Title", session.PeerName);
        TitleText.Text = Title;
        SubtitleText.Text = Localization.Get("Video_Subtitle");
        WaitingText.Text = Localization.Format("Video_Waiting", session.PeerName);
        ChatArea.Glass = true;
        ChatOverlaySubtitle.Text = Localization.Format("Viewer_ChatWith", session.PeerName);
        _audioLocal = session.IsAudioLocal;
        UpdateAudioVisuals();

        ChatArea.MessageSent += text => _ = session.SendChatAsync(text);

        _session.ChatReceived += OnChatReceived;
        _session.AudioStateChanged += OnAudioStateChanged;
        _session.AudioError += OnAudioError;
        _session.VideoStateChanged += OnVideoStateChanged;
        _session.LocalVideoFrame += OnLocalFrame;
        _session.RemoteVideoFrame += OnRemoteFrame;
        _session.VideoError += OnVideoError;
        _session.RemoteVideoEnded += OnRemoteVideoEnded;
        _session.SessionEnded += OnSessionEnded;

        Loaded += (_, _) =>
        {
            Ui.TrimWindowToScreen(this);
            if (_autoStart) BeginCall();
            _ = LoadDevicesAsync();
        };
        Closed += OnClosedInternal;
    }

    private async Task LoadDevicesAsync()
    {
        List<MediaDeviceInfo> cameras;
        List<MediaDeviceInfo> microphones;
        try
        {
            cameras = await Task.Run(() => _session.GetCameraDevices().ToList());
            microphones = await Task.Run(() => _session.GetMicrophoneDevices().ToList());
        }
        catch
        {
            return;
        }

        if (_closed) return;
        if (cameras.Count == 0)
            cameras.Add(new MediaDeviceInfo(0, Localization.Get("Video_DefaultDevice"), "0"));
        if (microphones.Count == 0)
            microphones.Add(new MediaDeviceInfo(0, Localization.Get("Video_DefaultDevice"), "0"));

        _loadingDevices = true;
        _cameraDevices.Clear();
        _cameraDevices.AddRange(cameras);
        _microphoneDevices.Clear();
        _microphoneDevices.AddRange(microphones);
        CameraBox.ItemsSource = _cameraDevices;
        MicBox.ItemsSource = _microphoneDevices;

        _activeCamera = FindDevice(_cameraDevices, _session.SelectedCamera) ?? _cameraDevices[0];
        _activeMicrophone = FindDevice(_microphoneDevices, _session.SelectedMicrophone) ?? _microphoneDevices[0];
        CameraBox.SelectedItem = _activeCamera;
        MicBox.SelectedItem = _activeMicrophone;
        _loadingDevices = false;
    }

    private static MediaDeviceInfo? FindDevice(List<MediaDeviceInfo> devices, MediaDeviceInfo? selected)
    {
        if (selected is null) return null;
        foreach (var device in devices)
            if (device.Id == selected.Id) return device;
        return null;
    }

    private void Camera_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices || CameraBox.SelectedItem is not MediaDeviceInfo device) return;
        if (_session.SelectCamera(device))
        {
            _activeCamera = device;
            return;
        }

        _loadingDevices = true;
        CameraBox.SelectedItem = _activeCamera;
        _loadingDevices = false;
    }

    private void Mic_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices || MicBox.SelectedItem is not MediaDeviceInfo device) return;
        if (_session.SelectMicrophone(device))
        {
            _activeMicrophone = device;
            return;
        }

        _loadingDevices = true;
        MicBox.SelectedItem = _activeMicrophone;
        _loadingDevices = false;
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && (source is Button || source.FindAncestorOfType<Button>() is not null))
            return;
        BeginMoveDrag(e);
    }

    private void PipLayer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_pipPlaced)
        {
            _pipPlaced = true;
            ResetPip();
            return;
        }
        SetPipBounds(CurrentPipLeft(), CurrentPipTop());
    }

    private void Pip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LocalPip).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ResetPip();
            e.Handled = true;
            return;
        }

        _pipDragging = true;
        _pipResizing = false;
        _pipGrabPoint = e.GetPosition(PipLayer);
        _pipStartLeft = CurrentPipLeft();
        _pipStartTop = CurrentPipTop();
        _pipStartWidth = LocalPip.Width;
        _pipStartHeight = LocalPip.Height;
        e.Pointer.Capture(LocalPip);
        e.Handled = true;
    }

    private void PipGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(LocalPip).Properties.IsLeftButtonPressed) return;
        _pipResizing = true;
        _pipDragging = false;
        _pipGrabPoint = e.GetPosition(PipLayer);
        _pipStartLeft = CurrentPipLeft();
        _pipStartTop = CurrentPipTop();
        _pipStartWidth = LocalPip.Width;
        _pipStartHeight = LocalPip.Height;
        e.Pointer.Capture(PipGrip);
        e.Handled = true;
    }

    private void Pip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pipDragging && !_pipResizing) return;
        var point = e.GetPosition(PipLayer);

        if (_pipResizing)
        {
            var aspect = _pipStartWidth / Math.Max(1, _pipStartHeight);
            var maxWidth = Math.Max(PipMinWidth, PipLayer.Bounds.Width - _pipStartLeft - 12);
            var width = Math.Clamp(_pipStartWidth + (point.X - _pipGrabPoint.X), PipMinWidth,
                Math.Min(760, maxWidth));
            var height = width / aspect;
            var maxHeight = Math.Max(90, PipLayer.Bounds.Height - _pipStartTop - 12);
            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * aspect;
            }

            LocalPip.Width = width;
            LocalPip.Height = height;
            SetPipBounds(_pipStartLeft, _pipStartTop);
        }
        else
        {
            SetPipBounds(
                _pipStartLeft + (point.X - _pipGrabPoint.X),
                _pipStartTop + (point.Y - _pipGrabPoint.Y));
        }

        e.Handled = true;
    }

    private void Pip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pipDragging && !_pipResizing) return;
        _pipDragging = false;
        _pipResizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ResetPip()
    {
        LocalPip.Width = PipDefaultWidth;
        LocalPip.Height = PipDefaultHeight;
        Canvas.SetLeft(LocalPip, Math.Max(8, PipLayer.Bounds.Width - PipDefaultWidth - 18));
        Canvas.SetTop(LocalPip, Math.Max(8, PipLayer.Bounds.Height - PipDefaultHeight - 18));
    }

    private void SetPipBounds(double left, double top)
    {
        var maxLeft = Math.Max(8, PipLayer.Bounds.Width - LocalPip.Width - 8);
        var maxTop = Math.Max(8, PipLayer.Bounds.Height - LocalPip.Height - 8);
        Canvas.SetLeft(LocalPip, Math.Clamp(left, 8, maxLeft));
        Canvas.SetTop(LocalPip, Math.Clamp(top, 8, maxTop));
    }

    private double CurrentPipLeft()
    {
        var value = Canvas.GetLeft(LocalPip);
        return double.IsNaN(value) ? 0 : value;
    }

    private double CurrentPipTop()
    {
        var value = Canvas.GetTop(LocalPip);
        return double.IsNaN(value) ? 0 : value;
    }

    public void BeginCall()
    {
        if (_callStarted) return;
        _callStarted = true;
        _session.StartAudioCall();
        _audioLocal = _session.IsAudioLocal;
        UpdateAudioVisuals();
        _session.StartVideo();
    }

    private void Audio_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
    }

    private void ToggleChat_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var show = !ChatPane.IsVisible;
        ChatPane.IsVisible = show;
        if (!show) return;

        _unread = 0;
        ChatButton.Content = Localization.Get("Viewer_Chat");
        ChatArea.FocusInput();
        Dispatcher.UIThread.Post(ChatArea.ScrollToEnd, DispatcherPriority.Background);
    }

    private void End_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

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
            UpdateAudioVisuals();
        });
    }

    private void UpdateAudioVisuals()
    {
        if (_audioLocal)
        {
            AudioButton.Content = Localization.Get("Video_MicOn");
            AudioButton.Background = CallActiveBrush;
            AudioButton.Foreground = Brushes.White;
            CallStatusText.Text = Localization.Get("Video_AudioActive");
        }
        else
        {
            AudioButton.Content = Localization.Get("Video_MicOff");
            AudioButton.ClearValue(BackgroundProperty);
            AudioButton.ClearValue(ForegroundProperty);
            CallStatusText.Text = Localization.Get("Video_AudioMuted");
        }
    }

    private void OnAudioError(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var text = EngineMessages.Translate(message);
            VideoStatusText.Text = text;
            if (!_closed)
                await Dialogs.InfoAsync(this, Localization.Get("Viewer_Audio"), text);
        });
    }

    private void OnVideoStateChanged(bool local, bool remote)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (remote)
                VideoStatusText.Text = "";
        });
    }

    private void OnVideoError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            VideoStatusText.Text = EngineMessages.Translate(message);
            SubtitleText.Text = Localization.Get("Video_NoCameraHint");
        });
    }

    private void OnRemoteFrame(byte[] jpeg)
    {
        lock (_remoteGate)
        {
            _pendingRemote = jpeg;
            if (_remoteRenderQueued) return;
            _remoteRenderQueued = true;
        }
        Dispatcher.UIThread.Post(RenderRemote, DispatcherPriority.Render);
    }

    private void RenderRemote()
    {
        byte[]? data;
        lock (_remoteGate)
        {
            data = _pendingRemote;
            _pendingRemote = null;
            _remoteRenderQueued = false;
        }

        if (data is null || _closed) return;

        try
        {
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            var previous = RemoteImage.Source as Bitmap;
            RemoteImage.Source = bitmap;
            previous?.Dispose();
            WaitingBox.IsVisible = false;
        }
        catch
        {
            // Un fotograma dañado se descarta.
        }
    }

    private void OnLocalFrame(byte[] jpeg)
    {
        lock (_localGate)
        {
            _pendingLocal = jpeg;
            if (_localRenderQueued) return;
            _localRenderQueued = true;
        }
        Dispatcher.UIThread.Post(RenderLocal, DispatcherPriority.Render);
    }

    private void RenderLocal()
    {
        byte[]? data;
        lock (_localGate)
        {
            data = _pendingLocal;
            _pendingLocal = null;
            _localRenderQueued = false;
        }

        if (data is null || _closed) return;

        try
        {
            using var stream = new MemoryStream(data);
            var bitmap = new Bitmap(stream);
            var previous = LocalImage.Source as Bitmap;
            LocalImage.Source = bitmap;
            previous?.Dispose();
        }
        catch
        {
            // Un fotograma dañado se descarta.
        }
    }

    private void OnRemoteVideoEnded()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_ending) return;
            _ending = true;
            try { _session.StopAudioCall(); } catch { }
            try { _session.StopVideo(); } catch { }
            if (!_closed)
                await Dialogs.InfoAsync(this, Localization.Get("Video_InviteTitle"),
                    Localization.Get("Video_EndedByPeer"));
            MarkClosed();
            Close();
        });
    }

    private void OnSessionEnded(string reason)
    {
        if (_ending) return;
        _ending = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try { _session.StopAudioCall(); } catch { }
            try { _session.StopVideo(); } catch { }
            if (_autoStart && !_closed)
                await Dialogs.InfoAsync(this, Localization.Get("Dlg_SessionTitle"),
                    EngineMessages.Translate(reason));
            MarkClosed();
            Close();
        });
    }

    private void MarkClosed() => _closed = true;

    private async void OnClosedInternal(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _ending = true;
        try { _session.StopAudioCall(); } catch { }
        try { _session.StopVideo(); } catch { }
        if (_autoStart)
        {
            try { await _session.EndSessionAsync(); } catch { }
        }
    }
}
