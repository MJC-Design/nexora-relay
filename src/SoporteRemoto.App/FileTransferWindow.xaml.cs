using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SoporteRemoto.Engine;

namespace SoporteRemoto.App;

public partial class FileTransferWindow : Window
{
    private static readonly Brush CallActiveBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
    private readonly ClientSession _session;
    private bool _finished;
    private bool _audioLocal;

    public FileTransferWindow(ClientSession session)
    {
        InitializeComponent();
        _session = session;

        Title = $"Transferencia de archivos — {session.HostName}";
        PeerText.Text = $"Conectado con {session.HostName} · Solo archivos, sin ver la pantalla";

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
        Closed += OnClosed;
        Loaded += (_, _) => ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(700, area.Width - 20));
        MinHeight = Math.Min(MinHeight, Math.Max(480, area.Height - 20));
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
            TransferText.Text = ex.Message;
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
            TransferText.Text = "Solicitando un archivo al otro equipo…";
            var accepted = await _session.RequestFileFromHostAsync();
            TransferText.Text = accepted
                ? "Esperando el archivo…"
                : "El otro equipo canceló la solicitud.";
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

    private void AudioCall_Click(object sender, RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
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

    private void OnChatReceived(string text, string? from) =>
        Dispatcher.BeginInvoke(() => ChatArea.AddIncoming(from ?? "La otra persona", text));

    private void OnAudioStateChanged(bool local, bool remote)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _audioLocal = local;
            if (local)
            {
                AudioCallButton.Content = "Finalizar llamada";
                AudioCallButton.Background = CallActiveBrush;
                AudioCallButton.Foreground = Brushes.White;
                AudioStatusText.Text = "Llamada de audio activa";
            }
            else
            {
                AudioCallButton.Content = "Iniciar llamada de audio";
                AudioCallButton.ClearValue(BackgroundProperty);
                AudioCallButton.ClearValue(ForegroundProperty);
                AudioStatusText.Text = remote ? "El otro equipo inició el audio · pulsa para hablar" : "";
            }
        });
    }

    private void OnAudioError(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AudioStatusText.Text = message;
            MessageBox.Show(this, message, "Llamada de audio", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnDisconnected(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_finished) return;
            _finished = true;
            MessageBox.Show(this, reason, "Sesión finalizada", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        });
    }

    private async void End_Click(object sender, RoutedEventArgs e)
    {
        _finished = true;
        try { _session.StopAudioCall(); } catch { }
        try { await _session.DisconnectAsync(); } catch { }
        Close();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _finished = true;
        try { _session.StopAudioCall(); } catch { }
        await _session.DisposeAsync();
    }
}
