using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Desktop;

public partial class MainWindow : Window
{
    private static readonly IBrush GreenBadge = Ui.BrushFrom("#E7FBF3");
    private static readonly IBrush GreenText = Ui.BrushFrom("#0B7D5D");
    private static readonly IBrush GreenDot = Ui.BrushFrom("#12B76A");
    private static readonly IBrush AmberBadge = Ui.BrushFrom("#FFF7E3");
    private static readonly IBrush AmberText = Ui.BrushFrom("#A85B00");
    private static readonly IBrush AmberDot = Ui.BrushFrom("#F79009");
    private static readonly IBrush GrayBadge = Ui.BrushFrom("#F2EDFC");
    private static readonly IBrush GrayText = Ui.BrushFrom("#6F678E");
    private static readonly IBrush GrayDot = Ui.BrushFrom("#A79BC7");

    private readonly HostController _host = new();
    private readonly AppSettings _settings;
    private readonly ObservableCollection<string> _log = new();
    private readonly string[] _languageNames = Localization.Languages.Select(l => l.Name).ToArray();

    private Window? _hostWindow;
    private Window? _sessionWindow;
    private bool _closing;
    private bool _connecting;
    private bool _languageReady;
    private string _relayStatus = "Desactivado";

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Ui.ApplyIcon(this);

        LanguageBox.ItemsSource = _languageNames;
        SettingsLanguageBox.ItemsSource = _languageNames;
        LogItems.ItemsSource = _log;
        AllowCheck.IsChecked = _settings.AllowConnections;

        _host.LogMessage += AppendLog;
        _host.SessionStateChanged += OnHostSessionChanged;
        _host.RelayStateChanged += OnRelayStateChanged;
        _host.CodeChanged += OnCodeChanged;
        _host.ApproveConnectionAsync = ApproveConnectionAsync;
        _host.SaveFileRequested = SaveFileFromRemoteAsync;
        _host.PickFileToSend = PickFileForRemoteAsync;

        _languageReady = true;
        SyncLanguageSelectors();
        RenderStatus();

        Loaded += async (_, _) => await OnLoadedAsync();
        Closed += (_, _) =>
        {
            _closing = true;
            _ = _host.DisposeAsync().AsTask();
        };
    }

    private async Task OnLoadedAsync()
    {
        Ui.TrimWindowToScreen(this);
        CodeText.Text = SessionCode.Format(_host.Code);
        RelayUrlBox.Text = _settings.RelayUrl;
        RefreshLanAddresses();

        if (AllowCheck.IsChecked == true)
            await StartHostAsync();

        CodeInput.Focus();
        await Task.Delay(120);
        PageScroller.Offset = new Vector(0, 0);
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(message));
            return;
        }

        var translated = EngineMessages.Translate(message);
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {translated}");
        while (_log.Count > 250) _log.RemoveAt(0);
        Dispatcher.UIThread.Post(
            () => LogScroll.Offset = new Vector(0, Math.Max(0, LogScroll.Extent.Height - LogScroll.Viewport.Height)),
            DispatcherPriority.Background);
    }

    private void OnRelayStateChanged(string status, string? id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _relayStatus = status;
            RenderStatus();
        });
    }

    private void RenderStatus()
    {
        RelayStatusText.Text = EngineMessages.TranslateRelayStatus(_relayStatus);
        switch (_relayStatus)
        {
            case "En línea":
                StatusBadge.Background = GreenBadge;
                StatusDot.Fill = GreenDot;
                RelayStatusText.Foreground = GreenText;
                break;
            case "Conectando al servidor…":
                StatusBadge.Background = AmberBadge;
                StatusDot.Fill = AmberDot;
                RelayStatusText.Foreground = AmberText;
                break;
            default:
                StatusBadge.Background = GrayBadge;
                StatusDot.Fill = GrayDot;
                RelayStatusText.Foreground = GrayText;
                break;
        }
    }

    private void OnCodeChanged(string code)
    {
        Dispatcher.UIThread.Post(() => CodeText.Text = SessionCode.Format(code));
    }

    private void OnHostSessionChanged(string? description)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (description is null)
            {
                _hostWindow?.Close();
                _hostWindow = null;
                return;
            }

            var session = _host.ActiveSession;
            if (session is null) return;

            if (_hostWindow is null || !_hostWindow.IsLoaded)
            {
                _hostWindow = session.Mode == SessionMode.Video
                    ? new VideoCallWindow(session, autoStart: true)
                    : new HostSessionWindow(session, description);
                _hostWindow.Closed += (_, _) => _hostWindow = null;
                _hostWindow.Show(this);
            }
        });
    }

    private Task<bool> ApproveConnectionAsync(ConnectionRequest request)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_closing)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var dialog = new AcceptConnectionWindow(request);
                var result = await dialog.ShowDialog<bool>(this);
                tcs.TrySetResult(result);
            }
            catch
            {
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    private Task<string?> SaveFileFromRemoteAsync(FileOffer offer)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_closing)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var accepted = await Dialogs.ConfirmAsync(this,
                    Localization.Get("Dlg_IncomingFile"),
                    Localization.Format("Dlg_FilePrompt", offer.FileName, Ui.FormatSize(offer.FileSize)),
                    Localization.Get("Dlg_SaveReceived"),
                    Localization.Get("Dlg_Cancel"));
                if (!accepted)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var path = await Dialogs.PickSaveFileAsync(this,
                    Localization.Get("Dlg_SaveReceived"), offer.FileName);
                tcs.TrySetResult(path);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    private Task<string?> PickFileForRemoteAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_closing)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var path = await Dialogs.PickOpenFileAsync(this, Localization.Get("Dlg_PickSend"));
                tcs.TrySetResult(path);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync(RelayUrlBox.Text?.Trim() ?? "");
            AppendLog(Localization.Get("Log_ConnectionsEnabled"));
            RefreshLanAddresses();
        }
        catch (Exception ex)
        {
            AllowCheck.IsChecked = false;
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_Error"),
                $"{Localization.Get("Dlg_EnableError")}: {EngineMessages.Translate(ex.Message)}");
        }
    }

    private async void AllowCheck_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_languageReady || _closing) return;

        _settings.AllowConnections = AllowCheck.IsChecked == true;
        _settings.Save();

        if (AllowCheck.IsChecked == true)
            await StartHostAsync();
        else
            await _host.StopAsync();
    }

    private void RefreshLanAddresses()
    {
        var ips = LocalNetwork.GetLocalIPv4();
        var text = ips.Count == 0
            ? Localization.Get("Home_LanNone")
            : string.Join("   ", ips.Select(ip => ip + ":" + _host.LanPort));
        LanAddressesText.Text = text;
        SettingsLanText.Text = text;
    }

    private async void CopyCode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(_host.Code);
            AppendLog(Localization.Get("Log_CodeCopied"));
        }
        catch
        {
            // El portapapeles no está disponible en este entorno.
        }
    }

    private async void NewCode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _host.RegenerateCode();
        CodeText.Text = SessionCode.Format(_host.Code);

        if (AllowCheck.IsChecked == true)
            await _host.SetRelayUrlAsync(RelayUrlBox.Text?.Trim() ?? "");
    }

    private async void SaveRelay_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var url = RelayUrlBox.Text?.Trim() ?? "";
        _settings.RelayUrl = url;
        _settings.Save();
        AppendLog(url.Length == 0
            ? Localization.Get("Log_ServerEmpty")
            : Localization.Format("Log_ServerSaved", url));

        if (AllowCheck.IsChecked == true)
            await _host.SetRelayUrlAsync(url);
    }

    private void Nav_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, NavFiles))
            ShowPage(FilesPage);
        else if (ReferenceEquals(sender, NavSettings))
            ShowPage(SettingsPage);
        else
            ShowPage(HomePage);

        if (ReferenceEquals(sender, NavFiles))
            FileCodeInput.Focus();
    }

    private void ShowPage(Control page)
    {
        HomePage.IsVisible = ReferenceEquals(page, HomePage);
        FilesPage.IsVisible = ReferenceEquals(page, FilesPage);
        SettingsPage.IsVisible = ReferenceEquals(page, SettingsPage);
        PageScroller.Offset = new Vector(0, 0);
    }

    private void GoSettings_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        NavSettings.IsChecked = true;
        ShowPage(SettingsPage);
    }

    private async void Language_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_languageReady) return;
        if (sender is not ComboBox combo || combo.SelectedIndex < 0) return;

        var code = Localization.Languages[combo.SelectedIndex].Code;
        if (code == Localization.Current) return;

        _settings.Language = code;
        _settings.Save();
        Localization.Apply(code);
        SyncLanguageSelectors();
        RefreshLanAddresses();
        RenderStatus();
    }

    private void SyncLanguageSelectors()
    {
        var index = Array.FindIndex(Localization.Languages, l => l.Code == Localization.Current);
        if (index < 0) index = 0;

        _languageReady = false;
        LanguageBox.SelectedIndex = index;
        SettingsLanguageBox.SelectedIndex = index;
        _languageReady = true;
    }

    private async void CodeInput_KeyDown(object? sender, KeyEventArgs e)
    {
        FixSeparatorBackspace(sender, e);
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectByCodeAsync(CodeInput.Text, SelectedMode(), CodeInput);
    }

    private async void FileCodeInput_KeyDown(object? sender, KeyEventArgs e)
    {
        FixSeparatorBackspace(sender, e);
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ConnectByCodeAsync(FileCodeInput.Text, SessionMode.Files, FileCodeInput);
    }

    private void CodeInput_TextChanged(object? sender, TextChangedEventArgs e) =>
        ApplyCodeMask(sender);

    private void FileCodeInput_TextChanged(object? sender, TextChangedEventArgs e) =>
        ApplyCodeMask(sender);

    private static void ApplyCodeMask(object? sender)
    {
        if (sender is not TextBox box) return;

        var text = box.Text ?? string.Empty;
        var formatted = FormatCodeMask(text);
        if (string.Equals(text, formatted, StringComparison.Ordinal)) return;

        var caret = Math.Min(box.CaretIndex, text.Length);
        var typedBefore = 0;
        for (var i = 0; i < caret; i++)
            if (SessionCode.IsAllowedChar(text[i])) typedBefore++;

        box.Text = formatted;

        var position = 0;
        var seen = 0;
        while (position < formatted.Length && seen < typedBefore)
        {
            if (SessionCode.IsAllowedChar(formatted[position])) seen++;
            position++;
        }
        box.CaretIndex = Math.Min(position, formatted.Length);
    }

    private static string FormatCodeMask(string text)
    {
        if (text.Length == 0) return string.Empty;

        var builder = new StringBuilder(SessionCode.Length + 2);
        var count = 0;
        foreach (var ch in text)
        {
            if (!SessionCode.IsAllowedChar(ch)) continue;
            builder.Append(char.ToUpperInvariant(ch));
            count++;
            if (count == SessionCode.Length) break;
            if (count % 3 == 0) builder.Append('-');
        }
        return builder.ToString();
    }

    private static void FixSeparatorBackspace(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Back || sender is not TextBox box) return;

        var text = box.Text;
        if (string.IsNullOrEmpty(text) || box.CaretIndex <= 0 || box.CaretIndex > text.Length) return;
        if (text[box.CaretIndex - 1] == '-') box.CaretIndex--;
    }

    private async void Connect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await ConnectByCodeAsync(CodeInput.Text, SelectedMode(), CodeInput);
    }

    private SessionMode SelectedMode() =>
        ModeVideo.IsChecked == true ? SessionMode.Video
        : ModeFiles.IsChecked == true ? SessionMode.Files
        : SessionMode.Screen;

    private async void FileConnect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ConnectByCodeAsync(FileCodeInput.Text, SessionMode.Files, FileCodeInput);

    private async Task ConnectByCodeAsync(string? rawCode, SessionMode mode, TextBox focusBox)
    {
        if (_sessionWindow is { IsVisible: true })
        {
            await Dialogs.InfoAsync(this, Localization.Get("App_WindowTitle"),
                Localization.Get("Dlg_AlreadySession"));
            return;
        }

        if (_connecting) return;

        var code = SessionCode.Normalize(rawCode);
        if (code.Length != SessionCode.Length)
        {
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_InvalidCodeTitle"),
                Localization.Get("Dlg_InvalidCode"));
            focusBox.Focus();
            return;
        }

        _connecting = true;
        SetConnecting(true);
        try
        {
            AppendLog(Localization.Format(
                mode switch
                {
                    SessionMode.Files => "Log_ConnectingFiles",
                    SessionMode.Video => "Log_ConnectingVideo",
                    _ => "Log_ConnectingCode",
                },
                SessionCode.Format(code)));

            var session = await ClientSession.ConnectWithCodeAsync(
                RelayUrlBox.Text?.Trim() ?? "", code, Environment.MachineName, mode,
                message => AppendLog(message));

            AppendLog(Localization.Format(
                mode switch
                {
                    SessionMode.Files => "Log_FileSession",
                    SessionMode.Video => "Log_VideoSession",
                    _ => "Log_Connected",
                },
                session.HostName));

            _sessionWindow = mode switch
            {
                SessionMode.Files => new FileTransferWindow(session),
                SessionMode.Video => new VideoCallWindow(session, autoStart: true),
                _ => new RemoteViewerWindow(session),
            };

            _sessionWindow.Closed += (_, _) => _sessionWindow = null;
            _sessionWindow.Show(this);
        }
        catch (Exception ex)
        {
            var message = EngineMessages.Translate(ex.Message);
            AppendLog(Localization.Format("Log_ConnectError", message));
            await Dialogs.InfoAsync(this, Localization.Get("Dlg_ConnectError"), message);
        }
        finally
        {
            _connecting = false;
            SetConnecting(false);
        }
    }

    private void SetConnecting(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        FileConnectButton.IsEnabled = !busy;
        ConnectLabel.IsVisible = !busy;
        ConnectBusyLabel.IsVisible = busy;
    }
}
