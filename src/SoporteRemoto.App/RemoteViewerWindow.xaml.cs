using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SoporteRemoto.Engine;

namespace SoporteRemoto.App;

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
    private int _unread;

    private static readonly Brush CallActiveBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));

    public RemoteViewerWindow(ClientSession session)
    {
        InitializeComponent();
        _session = session;

        Title = $"Soporte remoto — {session.HostName}";
        StatusText.Text = "Sesión activa";
        InfoText.Text = $"Conectado a {session.HostName} · Resolución remota {session.ScreenWidth}×{session.ScreenHeight}";

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
        _session.SaveFileRequested = SaveIncomingFileAsync;
        ChatArea.MessageSent += OnChatSent;

        Loaded += (_, _) =>
        {
            ClampToWorkArea();
            ScreenImage.Focus();
        };
        Deactivated += (_, _) => ReleaseKeys();
        Closed += OnClosed;
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(600, area.Width - 20));
        MinHeight = Math.Min(MinHeight, Math.Max(400, area.Height - 20));
        Width = Math.Min(Width, area.Width - 20);
        Height = Math.Min(Height, area.Height - 20);
        if (Left < area.Left) Left = area.Left;
        if (Top < area.Top) Top = area.Top;
        if (Left + Width > area.Right) Left = Math.Max(area.Left, area.Right - Width);
        if (Top + Height > area.Bottom) Top = Math.Max(area.Top, area.Bottom - Height);
    }

    private Task<string?> SaveIncomingFileAsync(FileOffer offer) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            if (_finished) return null;
            var dialog = new SaveFileDialog
            {
                FileName = offer.FileName,
                Title = "Guardar archivo recibido",
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }).Task;

    private void OnFrameReceived(byte[] jpeg)
    {
        lock (_frameGate)
        {
            _pendingFrame = jpeg;
            if (_renderQueued) return;
            _renderQueued = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderFrame));
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
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(data))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            _frameWidth = bitmap.PixelWidth;
            _frameHeight = bitmap.PixelHeight;
            ScreenImage.Source = bitmap;
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
        Dispatcher.BeginInvoke(new Action(UpdateCursorVisual));
    }

    private void UpdateCursorVisual()
    {
        if (_frameWidth == 0 || _frameHeight == 0 || _session.ScreenWidth == 0 || _session.ScreenHeight == 0)
            return;

        var (ox, oy, w, h) = GetDisplayedRect();
        if (w <= 0 || h <= 0) return;

        var px = ox + _cursorX * w / _session.ScreenWidth;
        var py = oy + _cursorY * h / _session.ScreenHeight;

        CursorDot.Visibility = Visibility.Visible;
        Canvas.SetLeft(CursorDot, px - CursorDot.Width / 2);
        Canvas.SetTop(CursorDot, py - CursorDot.Height / 2);
    }

    private (double Ox, double Oy, double W, double H) GetDisplayedRect()
    {
        var cw = ScreenImage.ActualWidth;
        var ch = ScreenImage.ActualHeight;
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

    private void ScreenImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        var now = Environment.TickCount64;
        if (now - _lastMouseMoveSent < 25) return;
        _lastMouseMoveSent = now;
        _session.SendInputMove(x, y);
    }

    private void ScreenImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ScreenImage.Focus();
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        _session.SendInputButton(ButtonIndex(e.ChangedButton), true, x, y);
        e.Handled = true;
    }

    private void ScreenImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;

        _session.SendInputButton(ButtonIndex(e.ChangedButton), false, x, y);
        e.Handled = true;
    }

    private static int ButtonIndex(MouseButton button) => button switch
    {
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => 0,
    };

    private void ScreenImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryMap(e.GetPosition(ScreenImage), out var x, out var y)) return;
        _session.SendInputWheel(e.Delta, x, y);
        e.Handled = true;
    }

    private void ScreenImage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0)
        {
            _pressedKeys.Add(vk);
            _session.SendInputKey(vk, true);
        }
        e.Handled = true;
    }

    private void ScreenImage_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk != 0)
        {
            _pressedKeys.Remove(vk);
            _session.SendInputKey(vk, false);
        }
        e.Handled = true;
    }

    private void ReleaseKeys()
    {
        if (_pressedKeys.Count == 0) return;
        foreach (var vk in _pressedKeys)
            _session.SendInputKey(vk, false);
        _pressedKeys.Clear();
    }

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecciona el archivo que quieres enviar" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            SendFileButton.IsEnabled = false;
            TransferText.Text = "Enviando archivo…";
            TransferBar.Value = 0;
            await _session.SendFileAsync(dialog.FileName);
            TransferText.Text = "Archivo enviado.";
            TransferBar.Value = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Transferencia", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SendFileButton.IsEnabled = true;
        }
    }

    private async void PullFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PullFileButton.IsEnabled = false;
            TransferText.Text = "Solicitando archivo al equipo remoto…";
            var accepted = await _session.RequestFileFromHostAsync();
            if (!accepted)
                TransferText.Text = "El equipo remoto canceló la solicitud.";
            else
                TransferText.Text = "Esperando el archivo…";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Solicitud de archivo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            PullFileButton.IsEnabled = true;
        }
    }

    private void OnChatSent(string text) => _session.SendChat(text);

    private void ToggleChat_Click(object sender, RoutedEventArgs e)
    {
        var show = ChatPane.Visibility != Visibility.Visible;
        ChatPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        _unread = 0;
        ChatButton.Content = "Chat";
        ChatArea.FocusInput();
    }

    private void Audio_Click(object sender, RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
    }

    private void OnChatReceived(string text, string? from)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ChatArea.AddIncoming(from ?? "La otra persona", text);
            if (ChatPane.Visibility == Visibility.Visible) return;

            _unread++;
            ChatButton.Content = $"Chat ({_unread})";
        });
    }

    private void OnAudioStateChanged(bool local, bool remote)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _audioLocal = local;
            if (local)
            {
                AudioButton.Content = "Finalizar llamada";
                AudioButton.Background = CallActiveBrush;
                AudioStatusText.Text = "Llamada de audio activa";
            }
            else
            {
                AudioButton.Content = "Llamada de audio";
                AudioButton.Background = Brushes.Transparent;
                AudioStatusText.Text = remote ? "Audio entrante · pulsa Llamada para hablar" : "";
            }
        });
    }

    private void OnAudioError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TransferText.Text = message;
            MessageBox.Show(this, message, "Llamada de audio", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            FullScreenButton.Content = "Salir de pantalla completa";
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            FullScreenButton.Content = "Pantalla completa";
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _finished = true;
        ReleaseKeys();
        try { await _session.DisconnectAsync(); } catch { }
        Close();
    }

    private void OnTransferProgress(int fileId, long received, long total)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TransferBar.Maximum = Math.Max(1, total);
            TransferBar.Value = Math.Min(received, TransferBar.Maximum);
            TransferText.Text = $"Recibiendo… {received * 100 / Math.Max(1, total)}%";
        });
    }

    private void OnTransferCompleted(int fileId, bool ok, string? error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TransferBar.Value = 0;
            TransferText.Text = ok ? "Archivo recibido correctamente." : "Recepción incompleta: " + error;
        });
    }

    private void OnOutgoingProgress(long sent, long total)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TransferBar.Maximum = Math.Max(1, total);
            TransferBar.Value = Math.Min(sent, TransferBar.Maximum);
            TransferText.Text = $"Enviando… {sent * 100 / Math.Max(1, total)}%";
        });
    }

    private void OnPullRequestResult(bool ok, string? message)
    {
        if (!ok && !string.IsNullOrWhiteSpace(message))
            Dispatcher.BeginInvoke(() => TransferText.Text = message);
    }

    private void OnSessionLog(string message)
    {
        Dispatcher.BeginInvoke(() => TransferText.Text = message);
    }

    private void OnDisconnected(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_finished) return;
            _finished = true;
            TransferText.Text = reason;
            MessageBox.Show(this, reason, "Sesión finalizada", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        });
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _finished = true;
        ReleaseKeys();
        try { _session.StopAudioCall(); } catch { }
        await _session.DisposeAsync();
    }
}
