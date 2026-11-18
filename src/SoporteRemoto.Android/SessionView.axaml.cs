using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SoporteRemoto.Android.Services;
using SoporteRemoto.Engine;

namespace SoporteRemoto.Android;

public partial class SessionView : UserControl
{
    private static readonly IBrush ToolbarButton = Ui.BrushFrom("#2E2450");
    private static readonly IBrush ArmedButton = Ui.BrushFrom("#8B5CF6");

    private readonly ClientSession _session;
    private readonly object _frameGate = new();

    private byte[]? _pendingFrame;
    private bool _renderQueued;
    private int _frameWidth;
    private int _frameHeight;
    private int _cursorX;
    private int _cursorY;
    private long _lastMoveSent;
    private bool _pressed;
    private int _activeButton;
    private bool _rightArmed;
    private bool _closed;
    private bool _disposed;
    private int _unread;

    public event EventHandler? Closed;

    public SessionView(ClientSession session)
    {
        InitializeComponent();
        _session = session;

        TitleText.Text = Localization.Format("Viewer_Title", session.HostName);
        InfoText.Text = Localization.Format("Viewer_Info",
            session.HostName, session.ScreenWidth, session.ScreenHeight);
        ChatArea.MessageSent += text => _session.SendChat(text);

        _session.FrameReceived += OnFrameReceived;
        _session.CursorChanged += OnCursorChanged;
        _session.Disconnected += OnDisconnected;
        _session.LogMessage += OnSessionLog;
        _session.TransferProgress += OnTransferProgress;
        _session.TransferCompleted += OnTransferCompleted;
        _session.OutgoingProgress += OnOutgoingProgress;
        _session.PullRequestResult += OnPullRequestResult;
        _session.ChatReceived += OnChatReceived;
        _session.SaveFileRequested = SaveIncomingFileAsync;
    }

    private void OnFrameReceived(byte[] jpeg)
    {
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

        if (data is null || _closed) return;

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

    private void Screen_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        _pressed = true;
        _activeButton = _rightArmed ? 1 : 0;
        if (_rightArmed)
        {
            _rightArmed = false;
            RightClickButton.Background = ToolbarButton;
        }

        _session.SendInputMove(x, y);
        _session.SendInputButton(_activeButton, true, x, y);
        e.Pointer.Capture(ScreenImage);
        e.Handled = true;
    }

    private void Screen_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        var now = Environment.TickCount64;
        var interval = _pressed ? 30 : 80;
        if (now - _lastMoveSent < interval) return;
        _lastMoveSent = now;
        _session.SendInputMove(x, y);
    }

    private void Screen_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;

        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;
        _session.SendInputButton(_activeButton, false, x, y);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void RightClick_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _rightArmed = !_rightArmed;
        RightClickButton.Background = _rightArmed ? ArmedButton : ToolbarButton;
        StatusText.Text = _rightArmed ? Localization.Get("Android_RightClick") : "";
    }

    private void Keyboard_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        KeyboardPanel.IsVisible = !KeyboardPanel.IsVisible;
        if (KeyboardPanel.IsVisible) TypeBox.Focus();
    }

    private void SendText_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var text = TypeBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return;

        _session.SendTextInput(text);
        TypeBox.Text = string.Empty;
        StatusText.Text = Localization.Get("Android_TypeSend");
    }

    private void ScrollUp_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _session.SendInputWheel(120, _session.ScreenWidth / 2, _session.ScreenHeight / 2);

    private void ScrollDown_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _session.SendInputWheel(-120, _session.ScreenWidth / 2, _session.ScreenHeight / 2);

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

    private async void SendFile_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Current;
        if (activity is null) return;

        var path = await AndroidFiles.PickFileAsync(activity);
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            StatusText.Text = Localization.Get("Dlg_SendingFile");
            await _session.SendFileAsync(path);
            StatusText.Text = Localization.Get("Viewer_FileSent");
        }
        catch (Exception ex)
        {
            await AndroidDialogs.InfoAsync(activity, Localization.Get("Transfer_Header"),
                EngineMessages.Translate(ex.Message));
        }
    }

    private void OnTransferProgress(int fileId, long received, long total)
    {
        var maximum = Math.Max(1, total);
        Dispatcher.UIThread.Post(() =>
            StatusText.Text = Localization.Format("Viewer_Receiving", received * 100 / maximum));
    }

    private void OnTransferCompleted(int fileId, bool ok, string? error)
    {
        Dispatcher.UIThread.Post(() => StatusText.Text = ok
            ? Localization.Get("Viewer_FileReceived")
            : Localization.Format("Viewer_FileIncomplete", error ?? ""));
    }

    private void OnOutgoingProgress(long sent, long total)
    {
        var maximum = Math.Max(1, total);
        Dispatcher.UIThread.Post(() =>
            StatusText.Text = Localization.Format("Viewer_Sending", sent * 100 / maximum));
    }

    private void OnPullRequestResult(bool ok, string? message)
    {
        if (ok || string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() => StatusText.Text = EngineMessages.Translate(message));
    }

    private void OnSessionLog(string message) =>
        Dispatcher.UIThread.Post(() => StatusText.Text = EngineMessages.Translate(message));

    private void OnDisconnected(string reason)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (_closed) return;
            _closed = true;

            var activity = MainActivity.Current;
            if (activity is not null)
                await AndroidDialogs.InfoAsync(activity, Localization.Get("Dlg_SessionTitle"),
                    EngineMessages.Translate(reason));

            await DisposeSessionAsync();
            Closed?.Invoke(this, EventArgs.Empty);
        });
    }

    private async void Disconnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_closed) return;

        var activity = MainActivity.Current;
        if (activity is not null)
        {
            var confirm = await AndroidDialogs.ConfirmAsync(activity,
                Localization.Get("Dlg_SessionTitle"),
                Localization.Get("Viewer_Disconnect"),
                Localization.Get("Dlg_OK"),
                Localization.Get("Dlg_Cancel"));
            if (!confirm) return;
        }

        _closed = true;
        try { await _session.DisconnectAsync(); } catch { }
        await DisposeSessionAsync();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string?> SaveIncomingFileAsync(FileOffer offer)
    {
        var activity = MainActivity.Current;
        if (activity is null) return null;

        var accepted = await AndroidDialogs.ConfirmAsync(activity,
            Localization.Get("Dlg_IncomingFile"),
            Localization.Format("Dlg_FilePrompt", offer.FileName, Ui.FormatSize(offer.FileSize)),
            Localization.Get("Dlg_SaveReceived"),
            Localization.Get("Dlg_Cancel"));
        if (!accepted) return null;

        var path = AndroidFiles.IncomingPath(activity, offer.FileName);
        Dispatcher.UIThread.Post(() => StatusText.Text = Localization.Format("Android_FileSaved", path));
        return path;
    }

    private async Task DisposeSessionAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _session.DisposeAsync(); } catch { }
        var previous = ScreenImage.Source as Bitmap;
        ScreenImage.Source = null;
        previous?.Dispose();
    }
}
