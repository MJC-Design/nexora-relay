using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SoporteRemoto.Engine;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.App;

public partial class MainWindow : Window
{
    private static readonly Brush GreenBadge = new SolidColorBrush(Color.FromRgb(0xE7, 0xFB, 0xF3));
    private static readonly Brush GreenText = new SolidColorBrush(Color.FromRgb(0x0B, 0x7D, 0x5D));
    private static readonly Brush AmberBadge = new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xE3));
    private static readonly Brush AmberText = new SolidColorBrush(Color.FromRgb(0xA8, 0x5B, 0x00));
    private static readonly Brush GrayBadge = new SolidColorBrush(Color.FromRgb(0xF2, 0xED, 0xFC));
    private static readonly Brush GrayDot = new SolidColorBrush(Color.FromRgb(0xA7, 0x9B, 0xC7));
    private static readonly Brush GrayText = new SolidColorBrush(Color.FromRgb(0x6F, 0x67, 0x8E));

    private readonly HostController _host = new();
    private readonly AppSettings _settings;
    private HostSessionWindow? _hostWindow;
    private Window? _sessionWindow;
    private bool _closing;
    private bool _connecting;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        _host.LogMessage += AppendLog;
        _host.SessionStateChanged += OnHostSessionChanged;
        _host.RelayStateChanged += OnRelayStateChanged;
        _host.CodeChanged += OnCodeChanged;
        _host.ApproveConnectionAsync = ApproveConnectionAsync;
        _host.SaveFileRequested = SaveFileFromRemoteAsync;
        _host.PickFileToSend = PickFileForRemoteAsync;

        Loaded += async (_, _) => await OnLoadedAsync();
        Closed += (_, _) =>
        {
            _closing = true;
            _ = _host.DisposeAsync().AsTask();
        };
    }

    private async Task OnLoadedAsync()
    {
        ClampToWorkArea();
        CodeText.Text = SessionCode.Format(_host.Code);
        RelayUrlBox.Text = _settings.RelayUrl;
        RefreshLanAddresses();

        if (AllowCheck.IsChecked == true)
            await StartHostAsync();

        CodeInput.Focus();
        Dispatcher.BeginInvoke(new Action(() => PageScroller.ScrollToTop()),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(700, area.Width - 20));
        MinHeight = Math.Min(MinHeight, Math.Max(480, area.Height - 20));
        Width = Math.Min(Width, area.Width - 20);
        Height = Math.Min(Height, area.Height - 20);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void OnRelayStateChanged(string status, string? id)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RelayStatusText.Text = status;
            switch (status)
            {
                case "En línea":
                    StatusBadge.Background = GreenBadge;
                    StatusDot.Fill = (Brush)FindResource("SuccessBrush");
                    RelayStatusText.Foreground = GreenText;
                    break;
                case "Conectando al servidor…":
                    StatusBadge.Background = AmberBadge;
                    StatusDot.Fill = (Brush)FindResource("WarningBrush");
                    RelayStatusText.Foreground = AmberText;
                    break;
                default:
                    StatusBadge.Background = GrayBadge;
                    StatusDot.Fill = GrayDot;
                    RelayStatusText.Foreground = GrayText;
                    break;
            }
        });
    }

    private void OnCodeChanged(string code)
    {
        Dispatcher.BeginInvoke(() => CodeText.Text = SessionCode.Format(code));
    }

    private void OnHostSessionChanged(string? description)
    {
        Dispatcher.BeginInvoke(() =>
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
                _hostWindow = new HostSessionWindow(session, description) { Owner = this };
                _hostWindow.Closed += (_, _) => _hostWindow = null;
                _hostWindow.Show();
            }
        });
    }

    private Task<bool> ApproveConnectionAsync(ConnectionRequest request) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_closing) return false;
            var dialog = new AcceptConnectionWindow(request) { Owner = this };
            return dialog.ShowDialog() == true;
        }).Task;

    private Task<string?> SaveFileFromRemoteAsync(FileOffer offer) =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            if (_closing) return null;

            var answer = MessageBox.Show(this,
                $"{offer.FileName}\nTamaño: {FormatSize(offer.FileSize)}\n\n¿Quieres aceptar este archivo?",
                "Archivo entrante", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return null;

            var dialog = new SaveFileDialog { FileName = offer.FileName, Title = "Guardar archivo recibido" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }).Task;

    private Task<string?> PickFileForRemoteAsync() =>
        Dispatcher.InvokeAsync<string?>(() =>
        {
            if (_closing) return null;
            var dialog = new OpenFileDialog { Title = "Selecciona el archivo que quieres enviar" };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }).Task;

    private async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync(RelayUrlBox.Text.Trim());
            AppendLog("Conexiones entrantes habilitadas.");
            RefreshLanAddresses();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "No se pudieron habilitar las conexiones: " + ex.Message,
                "SoporteRemoto", MessageBoxButton.OK, MessageBoxImage.Error);
            AllowCheck.IsChecked = false;
        }
    }

    private async void AllowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        if (AllowCheck.IsChecked == true)
            await StartHostAsync();
        else
            await _host.StopAsync();
    }

    private void RefreshLanAddresses()
    {
        var ips = LocalNetwork.GetLocalIPv4();
        var text = ips.Count == 0
            ? "No se detectó ninguna red local."
            : string.Join("   ", ips.Select(ip => ip + ":" + _host.LanPort));
        LanAddressesText.Text = text;
        SettingsLanText.Text = text;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_host.Code);
            AppendLog("Código copiado al portapapeles.");
        }
        catch { }
    }

    private async void NewCode_Click(object sender, RoutedEventArgs e)
    {
        _host.RegenerateCode();
        CodeText.Text = SessionCode.Format(_host.Code);

        if (AllowCheck.IsChecked == true)
            await _host.SetRelayUrlAsync(RelayUrlBox.Text.Trim());
    }

    private async void SaveRelay_Click(object sender, RoutedEventArgs e)
    {
        var url = RelayUrlBox.Text.Trim();
        _settings.RelayUrl = url;
        _settings.Save();
        AppendLog("Servidor guardado: " + (url.Length == 0 ? "(vacío)" : url));

        if (AllowCheck.IsChecked == true)
            await _host.SetRelayUrlAsync(url);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null) return;

        HomePage.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FilesPage.Visibility = NavFiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavFiles.IsChecked == true)
            FileCodeInput.Focus();
    }

    private void GoSettings_Click(object sender, RoutedEventArgs e) => NavSettings.IsChecked = true;

    private void CodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var mode = ModeFiles.IsChecked == true ? SessionMode.Files : SessionMode.Screen;
        _ = ConnectByCodeAsync(CodeInput.Text, mode);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var mode = ModeFiles.IsChecked == true ? SessionMode.Files : SessionMode.Screen;
        await ConnectByCodeAsync(CodeInput.Text, mode);
    }

    private void FileCodeInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = ConnectByCodeAsync(FileCodeInput.Text, SessionMode.Files);
    }

    private async void FileConnect_Click(object sender, RoutedEventArgs e) =>
        await ConnectByCodeAsync(FileCodeInput.Text, SessionMode.Files);

    private async Task ConnectByCodeAsync(string rawCode, SessionMode mode)
    {
        if (_sessionWindow is { IsLoaded: true })
        {
            MessageBox.Show(this, "Ya hay una ventana de sesión abierta.", "SoporteRemoto",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_connecting) return;

        var code = SessionCode.Normalize(rawCode);
        if (code.Length != SessionCode.Length)
        {
            MessageBox.Show(this,
                "Escribe el código de 9 caracteres (letras y números) que aparece en el otro equipo.",
                "Código no válido", MessageBoxButton.OK, MessageBoxImage.Information);
            (mode == SessionMode.Files ? FileCodeInput : CodeInput).Focus();
            return;
        }

        _connecting = true;
        SetConnecting(ConnectButton, true);
        SetConnecting(FileConnectButton, true);
        try
        {
            AppendLog((mode == SessionMode.Files
                    ? "Conectando para transferir archivos con el código "
                    : "Conectando con el código ") + SessionCode.Format(code) + "…");

            var session = await ClientSession.ConnectWithCodeAsync(
                RelayUrlBox.Text.Trim(), code, Environment.MachineName, mode, AppendLog);

            AppendLog(mode == SessionMode.Files
                ? $"Sesión de archivos con {session.HostName}."
                : $"Conectado a {session.HostName}.");

            if (mode == SessionMode.Files)
                _sessionWindow = new FileTransferWindow(session) { Owner = this };
            else
                _sessionWindow = new RemoteViewerWindow(session) { Owner = this };

            _sessionWindow.Closed += (_, _) => _sessionWindow = null;
            _sessionWindow.Show();
        }
        catch (Exception ex)
        {
            AppendLog("Error de conexión: " + ex.Message);
            MessageBox.Show(this, ex.Message, "No se pudo conectar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _connecting = false;
            SetConnecting(ConnectButton, false);
            SetConnecting(FileConnectButton, false);
        }
    }

    private static void SetConnecting(Button button, bool busy)
    {
        button.IsEnabled = !busy;
        button.Content = busy
            ? "Conectando…"
            : button.Name == "FileConnectButton"
                ? "Conectar para transferir"
                : "Conectar ahora";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }
}
