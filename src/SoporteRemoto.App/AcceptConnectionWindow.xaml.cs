using System.Windows;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.App;

public partial class AcceptConnectionWindow : Window
{
    public AcceptConnectionWindow(ConnectionRequest request)
    {
        InitializeComponent();
        ClientText.Text = $"El equipo «{request.ClientName}» quiere conectarse.";
        AddressText.Text = request.RemoteAddress == "Internet"
            ? "Conexión a través de Internet."
            : "Dirección: " + request.RemoteAddress;

        if (request.Mode == SessionMode.Files)
        {
            SubtitleText.Text = "Quieren enviarte o recibir archivos";
            IconGlyph.Text = "\uE8B7";
            NoticeBox.Background = System.Windows.Media.Brushes.White;
            NoticeBox.BorderBrush = (System.Windows.Media.Brush)FindResource("SoftBorderBrush");
            NoticeBox.BorderThickness = new Thickness(1);
            NoticeBox.CornerRadius = new CornerRadius(12);
            NoticeText.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
            NoticeText.Text =
                "En este modo la otra persona no puede ver tu pantalla ni controlar el ratón. " +
                "Solo podrán intercambiar archivos, chatear y hablar por audio.";
            AcceptButton.Content = "Aceptar transferencia";
        }
        else
        {
            NoticeText.Text =
                "Si aceptas, esta persona podrá ver tu pantalla y controlar el ratón y el teclado de este equipo.";
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Reject_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
