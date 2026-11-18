using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.App;

public partial class HostSessionWindow : Window
{
    private static readonly Brush CallActiveBrush = new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69));
    private readonly HostSession _session;
    private bool _ended;
    private bool _audioLocal;

    public HostSessionWindow(HostSession session, string description)
    {
        InitializeComponent();
        _session = session;
        PeerText.Text = description;

        if (session.Mode == SessionMode.Files)
        {
            Title = "Transferencia de archivos";
            SessionTitleText.Text = "Transferencia de archivos";
            SessionSubtitleText.Text = "Nadie puede ver ni controlar tu pantalla";
            TitleGlyph.Text = "\uE8B7";
        }

        ChatArea.MessageSent += text => _ = _session.SendChatAsync(text);
        _session.ChatReceived += OnChatReceived;
        _session.AudioStateChanged += OnAudioStateChanged;
        _session.AudioError += OnAudioError;

        Closed += OnClosedInternal;
    }

    private async void OnClosedInternal(object? sender, EventArgs e)
    {
        if (_ended) return;
        _ended = true;
        await _session.EndAsync();
    }

    private void OnChatReceived(string text, string? from) =>
        Dispatcher.BeginInvoke(() => ChatArea.AddIncoming(from ?? "La otra persona", text));

    private void AudioCall_Click(object sender, RoutedEventArgs e)
    {
        if (_audioLocal)
            _session.StopAudioCall();
        else
            _session.StartAudioCall();
    }

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
                AudioStatusText.Text = remote
                    ? "La otra persona inició el audio · pulsa para hablar"
                    : "";
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

    private async void SendFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Selecciona el archivo que quieres enviar" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await _session.SendFileAsync(dialog.FileName);
            MessageBox.Show(this, "Archivo enviado.", "Transferencia completada",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Transferencia",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void End_Click(object sender, RoutedEventArgs e)
    {
        _ended = true;
        try { _session.StopAudioCall(); } catch { }
        await _session.EndAsync();
        Close();
    }
}
