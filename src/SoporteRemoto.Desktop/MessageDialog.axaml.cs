using Avalonia.Controls;
using Avalonia.Styling;

namespace SoporteRemoto.Desktop;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        Ui.ApplyIcon(this);
    }

    public MessageDialog(string title, string message, string primaryText,
        string? secondaryText, bool danger) : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;

        if (danger && this.TryFindResource("DangerButtonTheme", out var theme) && theme is ControlTheme controlTheme)
            PrimaryButton.Theme = controlTheme;

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.IsVisible = true;
        }
    }

    private void Primary_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);

    private void Secondary_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
