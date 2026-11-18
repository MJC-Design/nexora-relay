using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using SoporteRemoto.Android.Services;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Android;

public partial class MainView : UserControl
{
    private readonly HostController _host = new();
    private readonly AppSettings _settings;
    private readonly ObservableCollection<string> _log = new();
    private readonly string[] _languageNames = Localization.Languages.Select(l => l.Name).ToArray();

    private TaskCompletionSource<bool>? _approval;
    private SessionView? _sessionView;
    private HostSessionView? _hostView;
    private bool _ready;
    private bool _syncingAllow;
    private bool _formattingCode;
    private string _relayStatus = "Desactivado";

    public MainView(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        LogItems.ItemsSource = _log;
        LanguageBox.ItemsSource = _languageNames;
        CodeText.Text = SessionCode.Format(_host.Code);
        RelayBox.Text = settings.RelayUrl;
        DeviceNameText.Text = DeviceInfo.Name;

        _host.LogMessage += AppendLog;
        _host.SessionStateChanged += OnHostSessionChanged;
        _host.RelayStateChanged += OnRelayStateChanged;
        _host.CodeChanged += OnCodeChanged;
        _host.ApproveConnectionAsync = ApproveConnectionAsync;
        _host.SaveFileRequested = SaveIncomingFileAsync;
        _host.PickFileToSend = PickFileToSendAsync;

        AllowSwitch.PropertyChanged += AllowSwitch_PropertyChanged;
        LanguageBox.SelectionChanged += Language_SelectionChanged;

        _ready = true;
        RefreshLocalized();
        RenderRelayStatus();
        UpdateShareState();

        Loaded += async (_, _) =>
        {
            UpdateLanAddresses();
            if (AllowSwitch.IsChecked == true)
                await StartHostAsync();
            UpdateAccessState();
        };
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
        while (_log.Count > 200) _log.RemoveAt(0);
    }

    private void OnRelayStateChanged(string status, string? id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _relayStatus = status;
            RenderRelayStatus();
        });
    }

    private void RenderRelayStatus()
    {
        RelayStatusText.Text = EngineMessages.TranslateRelayStatus(_relayStatus);
    }

    private void OnCodeChanged(string code) =>
        Dispatcher.UIThread.Post(() => CodeText.Text = SessionCode.Format(code));

    private async void AllowSwitch_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_ready || _syncingAllow || e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;

        _settings.AllowConnections = AllowSwitch.IsChecked == true;
        _settings.Save();

        if (AllowSwitch.IsChecked == true)
            await StartHostAsync();
        else
            await StopHostAsync();
    }

    private async Task StartHostAsync()
    {
        if (_host.IsRunning) return;

        try
        {
            await _host.StartAsync(RelayBox.Text?.Trim() ?? "");
            AppendLog(Localization.Get("Log_ConnectionsEnabled"));
            UpdateLanAddresses();
        }
        catch (Exception ex)
        {
            _syncingAllow = true;
            AllowSwitch.IsChecked = false;
            _syncingAllow = false;
            _settings.AllowConnections = false;
            _settings.Save();
            await ShowInfoAsync(Localization.Get("Dlg_Error"),
                $"{Localization.Get("Dlg_EnableError")}: {EngineMessages.Translate(ex.Message)}");
        }
    }

    private async Task StopHostAsync()
    {
        try { await _host.StopAsync(); } catch { }
    }

    private void UpdateLanAddresses()
    {
        var ips = LocalNetwork.GetLocalIPv4();
        LanText.Text = ips.Count == 0
            ? Localization.Get("Home_LanNone")
            : string.Join("   ", ips.Select(ip => ip + ":" + _host.LanPort));
    }

    private void Nav_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var page = sender as Avalonia.Controls.Button;
        HomePage.IsVisible = ReferenceEquals(page, HomeNav);
        ConnectPage.IsVisible = ReferenceEquals(page, ConnectNav);
        SettingsPage.IsVisible = ReferenceEquals(page, SettingsNav);

        HomeNav.Classes.Set("active", ReferenceEquals(page, HomeNav));
        ConnectNav.Classes.Set("active", ReferenceEquals(page, ConnectNav));
        SettingsNav.Classes.Set("active", ReferenceEquals(page, SettingsNav));

        UpdateShareState();
        UpdateAccessState();
    }

    private async void NewCode_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _host.RegenerateCode();
        CodeText.Text = SessionCode.Format(_host.Code);

        if (AllowSwitch.IsChecked == true)
            await _host.SetRelayUrlAsync(RelayBox.Text?.Trim() ?? "");
    }

    private async void Share_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Current;
        if (activity is null) return;

        ShareButton.IsEnabled = false;
        try
        {
            var started = await ScreenShare.StartAsync(activity);
            if (!started)
            {
                await ShowInfoAsync(Localization.Get("Dlg_Error"),
                    Localization.Get("Android_ScreenPermissionDenied"));
            }
            else if (AllowSwitch.IsChecked == true && !_host.IsRunning)
            {
                await StartHostAsync();
            }
        }
        finally
        {
            ShareButton.IsEnabled = true;
            UpdateShareState();
        }
    }

    private void StopShare_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Current;
        if (activity is not null) ScreenShare.Stop(activity);
        UpdateShareState();
    }

    private void UpdateShareState()
    {
        var sharing = AndroidScreenSharing.IsReady;
        ShareButton.IsVisible = !sharing;
        StopShareButton.IsVisible = sharing;
        ShareStatusText.Text = sharing
            ? Localization.Get("Android_SharingNow")
            : Localization.Get("Android_SharingStopped");
    }

    private void Access_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var activity = MainActivity.Current;
        if (activity is not null) AndroidSettingsHelper.OpenAccessibility(activity);
    }

    private void UpdateAccessState()
    {
        var activity = MainActivity.Current;
        var enabled = activity is not null && AndroidRemoteInput.IsEnabled(activity);
        AccessButton.Content = enabled
            ? Localization.Get("Android_AccessOn")
            : Localization.Get("Android_AccessOff");
        AccessStatusText.Text = enabled
            ? Localization.Get("Android_ControlHint")
            : Localization.Get("Android_AccessHint");
    }

    private void CodeInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_formattingCode) return;

        var raw = CodeInput.Text ?? "";
        var clean = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (clean.Length > SessionCode.Length) clean = clean[..SessionCode.Length];

        var formatted = clean.Length <= 3
            ? clean
            : clean.Length <= 6
                ? $"{clean[..3]} {clean[3..]}"
                : $"{clean[..3]} {clean[3..6]} {clean[6..]}";

        if (formatted == raw) return;

        _formattingCode = true;
        CodeInput.Text = formatted;
        CodeInput.CaretIndex = formatted.Length;
        _formattingCode = false;
    }

    private async void Connect_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ConnectAsync();

    private async Task ConnectAsync()
    {
        if (_sessionView is not null)
        {
            await ShowInfoAsync(Localization.Get("App_WindowTitle"), Localization.Get("Dlg_AlreadySession"));
            return;
        }

        var code = SessionCode.Normalize(CodeInput.Text);
        if (code.Length != SessionCode.Length)
        {
            await ShowInfoAsync(Localization.Get("Dlg_InvalidCodeTitle"), Localization.Get("Dlg_InvalidCode"));
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            AppendLog(Localization.Format("Log_ConnectingCode", SessionCode.Format(code)));
            var mode = ModeBox.SelectedIndex == 1 ? SessionMode.Files : SessionMode.Screen;
            var session = await ClientSession.ConnectWithCodeAsync(
                RelayBox.Text?.Trim() ?? "", code, DeviceInfo.Name, mode, AppendLog);

            AppendLog(Localization.Format("Log_Connected", session.HostName));
            OpenSessionView(session);
        }
        catch (Exception ex)
        {
            var message = EngineMessages.Translate(ex.Message);
            AppendLog(Localization.Format("Log_ConnectError", message));
            await ShowInfoAsync(Localization.Get("Dlg_ConnectError"), message);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void OpenSessionView(ClientSession session)
    {
        var view = new SessionView(session);
        view.Closed += (_, _) =>
        {
            RootGrid.Children.Remove(view);
            if (ReferenceEquals(_sessionView, view)) _sessionView = null;
        };

        _sessionView = view;
        Grid.SetRowSpan(view, 3);
        Grid.SetRow(view, 0);
        RootGrid.Children.Add(view);
    }

    private void OnHostSessionChanged(string? description)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (description is null)
            {
                CloseHostView();
                return;
            }

            var session = _host.ActiveSession;
            if (session is null) return;

            if (_hostView is null)
            {
                _hostView = new HostSessionView(session, description);
                _hostView.Closed += (_, _) => CloseHostView();
                Grid.SetRowSpan(_hostView, 3);
                RootGrid.Children.Add(_hostView);
                UpdateShareState();
            }
        });
    }

    private void CloseHostView()
    {
        var view = _hostView;
        _hostView = null;
        if (view is not null) RootGrid.Children.Remove(view);
    }

    private async Task<bool> ApproveConnectionAsync(ConnectionRequest request)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            _approval = tcs;
            AcceptText.Text = $"{request.ClientName} · {ModeName(request.Mode)}";
            AcceptAddress.Text = request.RemoteAddress;
            AcceptOverlay.IsVisible = true;
        });
        return await tcs.Task;
    }

    private void Accept_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ResolveApproval(true);

    private void Reject_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ResolveApproval(false);

    private void ResolveApproval(bool accepted)
    {
        AcceptOverlay.IsVisible = false;
        _approval?.TrySetResult(accepted);
        _approval = null;
    }

    private static string ModeName(SessionMode mode) => mode switch
    {
        SessionMode.Files => Localization.Get("Home_ModeFiles"),
        SessionMode.Video => Localization.Get("Home_ModeVideo"),
        _ => Localization.Get("Home_ModeScreen"),
    };

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
        await AndroidDialogs.InfoAsync(activity, Localization.Get("Dlg_IncomingFile"),
            Localization.Format("Android_FileSaved", path));
        return path;
    }

    private async Task<string?> PickFileToSendAsync()
    {
        var activity = MainActivity.Current;
        return activity is null ? null : await AndroidFiles.PickFileAsync(activity);
    }

    private void SaveRelay_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var url = RelayBox.Text?.Trim() ?? "";
        _settings.RelayUrl = url;
        _settings.Save();
        AppendLog(url.Length == 0
            ? Localization.Get("Log_ServerEmpty")
            : Localization.Format("Log_ServerSaved", url));

        if (AllowSwitch.IsChecked == true)
            _ = _host.SetRelayUrlAsync(url);
    }

    private void Language_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedIndex < 0) return;

        var code = Localization.Languages[LanguageBox.SelectedIndex].Code;
        if (code == Localization.Current) return;

        _settings.Language = code;
        _settings.Save();
        Localization.Apply(code);
        RefreshLocalized();
    }

    private void RefreshLocalized()
    {
        ModeBox.ItemsSource = new[]
        {
            Localization.Get("Home_ModeScreen"),
            Localization.Get("Home_ModeFiles"),
        };
        if (ModeBox.SelectedIndex < 0) ModeBox.SelectedIndex = 0;

        var index = Array.FindIndex(Localization.Languages, l => l.Code == Localization.Current);
        if (index < 0) index = 0;
        _ready = false;
        LanguageBox.SelectedIndex = index;
        _ready = true;

        RenderRelayStatus();
        UpdateLanAddresses();
        UpdateShareState();
        UpdateAccessState();
        DeviceNameText.Text = DeviceInfo.Name;
    }

    private Task ShowInfoAsync(string title, string text)
    {
        var activity = MainActivity.Current;
        return activity is null ? Task.CompletedTask : AndroidDialogs.InfoAsync(activity, title, text);
    }
}
