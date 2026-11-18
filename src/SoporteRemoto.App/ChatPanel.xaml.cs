using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoporteRemoto.App;

public partial class ChatPanel : UserControl
{
    private static readonly Brush MineBubble = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly Brush OtherBubble = Brushes.White;
    private static readonly Brush OtherBorder = new SolidColorBrush(Color.FromRgb(0xEA, 0xE3, 0xF9));
    private static readonly Brush MineText = Brushes.White;
    private static readonly Brush OtherText = new SolidColorBrush(Color.FromRgb(0x2E, 0x25, 0x56));
    private static readonly Brush MineMeta = new SolidColorBrush(Color.FromRgb(0xE9, 0xD8, 0xFF));
    private static readonly Brush OtherMeta = new SolidColorBrush(Color.FromRgb(0x9A, 0x93, 0xB8));

    private readonly ObservableCollection<ChatEntry> _entries = new();

    public event Action<string>? MessageSent;

    public ChatPanel()
    {
        InitializeComponent();
        Messages.ItemsSource = _entries;
    }

    public void AddOutgoing(string text) => Add("Yo", text, isMine: true);

    public void AddIncoming(string sender, string text) =>
        Add(string.IsNullOrWhiteSpace(sender) ? "La otra persona" : sender, text, isMine: false);

    public void FocusInput() => Input.Focus();

    private void Add(string sender, string text, bool isMine)
    {
        _entries.Add(new ChatEntry
        {
            Sender = sender,
            Text = text,
            Time = DateTime.Now.ToString("HH:mm"),
            Alignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleBrush = isMine ? MineBubble : OtherBubble,
            BorderBrush = isMine ? MineBubble : OtherBorder,
            TextBrush = isMine ? MineText : OtherText,
            SenderBrush = isMine ? MineMeta : OtherMeta,
        });

        Dispatcher.BeginInvoke(new Action(() => Scroll.ScrollToEnd()));
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCurrent();

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendCurrent();
    }

    private void SendCurrent()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;

        Input.Clear();
        AddOutgoing(text);
        MessageSent?.Invoke(text);
        Input.Focus();
    }

    public sealed class ChatEntry
    {
        public string Sender { get; init; } = "";
        public string Text { get; init; } = "";
        public string Time { get; init; } = "";
        public HorizontalAlignment Alignment { get; init; }
        public Brush BubbleBrush { get; init; } = Brushes.White;
        public Brush BorderBrush { get; init; } = Brushes.Transparent;
        public Brush TextBrush { get; init; } = Brushes.Black;
        public Brush SenderBrush { get; init; } = Brushes.Gray;
    }
}
