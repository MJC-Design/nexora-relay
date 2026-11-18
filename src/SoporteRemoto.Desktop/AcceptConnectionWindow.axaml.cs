using Avalonia.Controls;
using Avalonia.Media;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Desktop;

public partial class AcceptConnectionWindow : Window
{
    public AcceptConnectionWindow()
    {
        InitializeComponent();
        Ui.ApplyIcon(this);
    }

    public AcceptConnectionWindow(ConnectionRequest request) : this()
    {
        Title = Localization.Get("Accept_Title");
        TitleText.Text = Localization.Get("Accept_Title");
        ClientText.Text = Localization.Format("Accept_Client", request.ClientName);
        AddressText.Text = request.RemoteAddress == "Internet"
            ? Localization.Get("Accept_Internet")
            : Localization.Format("Accept_Address", request.RemoteAddress);

        if (request.Mode == SessionMode.Files)
        {
            SubtitleText.Text = Localization.Get("Accept_SubtitleFiles");
            ModeText.Text = Localization.Get("Home_ModeFiles");
            NoticeText.Text = Localization.Get("Accept_NoticeFiles");
            NoticeBox.Background = Ui.BrushFrom("#F3FBF7");
            NoticeBox.BorderBrush = Ui.BrushFrom("#CDEEDD");
            NoticeText.Foreground = Ui.BrushFrom("#1B7A55");
            NoticeIcon.Foreground = Ui.BrushFrom("#18A66A");
            AcceptButton.Content = Localization.Get("Accept_AcceptFiles");
        }
        else if (request.Mode == SessionMode.Video)
        {
            SubtitleText.Text = Localization.Get("Accept_SubtitleVideo");
            ModeText.Text = Localization.Get("Home_ModeVideo");
            NoticeText.Text = Localization.Get("Accept_NoticeVideo");
            NoticeBox.Background = Ui.BrushFrom("#FFF3F9");
            NoticeBox.BorderBrush = Ui.BrushFrom("#F7CFE3");
            NoticeText.Foreground = Ui.BrushFrom("#A3245F");
            NoticeIcon.Foreground = Ui.BrushFrom("#E8438B");
            ModeBadge.Background = Ui.BrushFrom("#FCE7F2");
            ModeText.Foreground = Ui.BrushFrom("#C22A6B");
            AcceptButton.Content = Localization.Get("Accept_AcceptVideo");
        }
        else
        {
            SubtitleText.Text = Localization.Get("Accept_SubtitleScreen");
            ModeText.Text = Localization.Get("Home_ModeScreen");
            NoticeText.Text = Localization.Get("Accept_NoticeScreen");
        }
    }

    private void Accept_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);

    private void Reject_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
