using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SoporteRemoto.Desktop;

public partial class ChatPanel : UserControl
{
    private static readonly IBrush MineBubble = Ui.BrushFrom("#8B5CF6");
    private static readonly IBrush MineBorder = Ui.BrushFrom("#8B5CF6");
    private static readonly IBrush OtherBubble = Brushes.White;
    private static readonly IBrush OtherBorder = Ui.BrushFrom("#EAE3F9");
    private static readonly IBrush MineText = Brushes.White;
    private static readonly IBrush OtherText = Ui.BrushFrom("#2E2556");
    private static readonly IBrush MineMeta = Ui.BrushFrom("#E9D8FF");
    private static readonly IBrush OtherMeta = Ui.BrushFrom("#9A93B8");

    private static readonly IBrush GlassMineBubble = Ui.BrushFrom("#B58B5CF6");
    private static readonly IBrush GlassMineBorder = Ui.BrushFrom("#40FFFFFF");
    private static readonly IBrush GlassOtherBubble = Ui.BrushFrom("#2EFFFFFF");
    private static readonly IBrush GlassOtherBorder = Ui.BrushFrom("#33FFFFFF");
    private static readonly IBrush GlassMineText = Brushes.White;
    private static readonly IBrush GlassOtherText = Ui.BrushFrom("#F4EFFF");
    private static readonly IBrush GlassMineMeta = Ui.BrushFrom("#F1E4FF");
    private static readonly IBrush GlassOtherMeta = Ui.BrushFrom("#CBBDEA");

    private readonly ObservableCollection<ChatEntry> _entries = new();
    private bool _glass;

    public event Action<string>? MessageSent;

    public bool Glass
    {
        get => _glass;
        set
        {
            if (_glass == value) return;
            _glass = value;
            if (value) InputBox.Classes.Add("glass");
            else InputBox.Classes.Remove("glass");
        }
    }

    public ChatPanel()
    {
        InitializeComponent();
        MessagesHost.ItemsSource = _entries;
    }

    public void AddOutgoing(string text) => Add(Localization.Get("Chat_Me"), text, isMine: true);

    public void AddIncoming(string? sender, string text) =>
        Add(string.IsNullOrWhiteSpace(sender) ? Localization.Get("Chat_Other") : sender!, text, isMine: false);

    public void FocusInput() => InputBox.Focus();

    public void ScrollToEnd() =>
        ChatScroll.Offset = new Vector(0, Math.Max(0, ChatScroll.Extent.Height - ChatScroll.Viewport.Height));

    private void Add(string sender, string text, bool isMine)
    {
        _entries.Add(new ChatEntry
        {
            Sender = sender,
            Text = text,
            Time = DateTime.Now.ToString("HH:mm"),
            Meta = $"{sender} · {DateTime.Now:HH:mm}",
            Alignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BubbleBrush = _glass ? (isMine ? GlassMineBubble : GlassOtherBubble) : (isMine ? MineBubble : OtherBubble),
            BorderBrush = _glass ? (isMine ? GlassMineBorder : GlassOtherBorder) : (isMine ? MineBorder : OtherBorder),
            TextBrush = _glass ? (isMine ? GlassMineText : GlassOtherText) : (isMine ? MineText : OtherText),
            SenderBrush = _glass ? (isMine ? GlassMineMeta : GlassOtherMeta) : (isMine ? MineMeta : OtherMeta),
        });

        Dispatcher.UIThread.Post(
            () => ChatScroll.Offset = new Vector(0, Math.Max(0, ChatScroll.Extent.Height - ChatScroll.Viewport.Height)),
            DispatcherPriority.Background);
    }

    private void Send_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SendCurrent();

    private void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SendCurrent();
    }

    private void SendCurrent()
    {
        var text = InputBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return;

        InputBox.Text = string.Empty;
        AddOutgoing(text);
        MessageSent?.Invoke(text);
        InputBox.Focus();
    }

    public sealed class ChatEntry
    {
        public string Sender { get; init; } = string.Empty;
        public string Meta { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string Time { get; init; } = string.Empty;
        public HorizontalAlignment Alignment { get; init; }
        public IBrush BubbleBrush { get; init; } = Brushes.White;
        public IBrush BorderBrush { get; init; } = Brushes.Transparent;
        public IBrush TextBrush { get; init; } = Brushes.Black;
        public IBrush SenderBrush { get; init; } = Brushes.Gray;
    }
}
