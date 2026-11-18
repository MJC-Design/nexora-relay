using System.Net;
using System.Net.Sockets;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Engine;

public sealed class HostController : IAsyncDisposable
{
    public event Action<string>? LogMessage;
    public event Action<string?>? SessionStateChanged;
    public event Action<string, string?>? RelayStateChanged;
    public event Action<string>? CodeChanged;

    /// <summary>Se invoca desde un hilo de fondo; la UI decide cómo preguntar al usuario.</summary>
    public Func<ConnectionRequest, Task<bool>>? ApproveConnectionAsync { get; set; }
    public Func<FileOffer, Task<string?>>? SaveFileRequested { get; set; }
    public Func<Task<string?>>? PickFileToSend { get; set; }

    public string Code { get; private set; } = SessionCode.Generate();
    public string? RelayId { get; private set; }
    public int LanPort { get; } = ProtocolConstants.DefaultLanPort;
    public bool IsRunning => _enabled;
    public HostSession? ActiveSession { get; private set; }

    private readonly object _gate = new();
    private bool _enabled;
    private bool _sessionBusy;
    private bool _disposed;
    private TcpListener? _listener;
    private LanDiscoveryHost? _discovery;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _relayCts;
    private Task? _relayTask;
    private string _relayUrl = "";

    public void RegenerateCode()
    {
        Code = SessionCode.Generate();
        Log("Nuevo código de sesión: " + SessionCode.Format(Code));
        CodeChanged?.Invoke(Code);
    }

    public void Log(string message) => LogMessage?.Invoke(message);

    public async Task StartAsync(string relayUrl)
    {
        if (_enabled) return;

        _enabled = true;
        _relayUrl = (relayUrl ?? "").Trim();
        _lifetime = new CancellationTokenSource();
        var ct = _lifetime.Token;

        _listener = new TcpListener(IPAddress.Any, LanPort);
        _listener.Start();
        Log($"Escuchando conexiones de red local en el puerto {LanPort}.");
        Log("Código de sesión: " + SessionCode.Format(Code));
        _ = LanAcceptLoopAsync(_listener, ct);

        try
        {
            _discovery = new LanDiscoveryHost(() => Code, LanPort);
            _discovery.Start();
        }
        catch (Exception ex)
        {
            _discovery?.Dispose();
            _discovery = null;
            Log("No se pudo activar la búsqueda en red local: " + ex.Message);
        }

        _relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _relayTask = RelayLoopAsync(_relayCts.Token);

        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_enabled) return;
        _enabled = false;

        HostSession? session;
        lock (_gate) session = ActiveSession;
        if (session is not null)
        {
            try { await session.EndAsync("Se desactivaron las conexiones."); } catch { }
        }

        try { _relayCts?.Cancel(); } catch { }
        try { _lifetime?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _discovery?.Dispose(); } catch { }

        try { if (_relayTask is not null) await _relayTask; } catch { }

        _relayTask = null;
        _relayCts?.Dispose();
        _relayCts = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _listener = null;
        _discovery = null;
        RelayId = null;
        RelayStateChanged?.Invoke("Desactivado", null);
        Log("Conexiones desactivadas.");
    }

    public async Task SetRelayUrlAsync(string relayUrl)
    {
        _relayUrl = (relayUrl ?? "").Trim();
        if (!_enabled) return;

        try { _relayCts?.Cancel(); } catch { }
        try { if (_relayTask is not null) await _relayTask; } catch { }
        _relayCts?.Dispose();

        _relayCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime!.Token);
        _relayTask = RelayLoopAsync(_relayCts.Token);
    }

    private async Task RelayLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(_relayUrl))
            {
                RelayStateChanged?.Invoke("Sin servidor configurado", null);
                return;
            }

            RelayHostConnection? conn = null;
            var retryNow = false;
            try
            {
                RelayStateChanged?.Invoke("Conectando al servidor…", null);
                conn = await RelayHostConnection.ConnectAsync(_relayUrl, Code, ct);
                RelayId = conn.SessionId;
                RelayStateChanged?.Invoke("En línea", conn.SessionId);
                Log("Conectado al servidor de Internet. Código: " + SessionCode.Format(conn.SessionId));

                while (!ct.IsCancellationRequested)
                {
                    var frame = await conn.ReceiveAsync(ct);
                    if (frame is null) break;
                    if (frame.Value.Type != MessageType.Control) continue;

                    var msg = ControlMessage.FromUtf8(frame.Value.Payload.Span);
                    if (msg is null) continue;

                    if (msg.Type == "connect")
                        await RunSessionAsync(conn, msg, "Internet", ownsConnection: false, ct);
                }
            }
            catch (RelayCodeTakenException)
            {
                RegenerateCode();
                retryNow = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log("Servidor: " + ex.Message);
            }
            finally
            {
                if (conn is not null)
                {
                    try { await conn.DisposeAsync(); } catch { }
                }
                RelayId = null;
                RelayStateChanged?.Invoke("Sin conexión al servidor", null);
            }

            if (retryNow) continue;

            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task LanAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception ex)
            {
                Log("Error de red local: " + ex.Message);
                continue;
            }

            _ = HandleLanClientAsync(client, ct);
        }
    }

    private async Task HandleLanClientAsync(TcpClient client, CancellationToken ct)
    {
        var conn = TcpConnection.FromAccepted(client);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var frame = await conn.ReceiveAsync(timeout.Token);
            if (frame is null)
            {
                await conn.DisposeAsync();
                return;
            }

            var msg = frame.Value.Type == MessageType.Control
                ? ControlMessage.FromUtf8(frame.Value.Payload.Span)
                : null;

            if (msg?.Type != "connect")
            {
                await conn.DisposeAsync();
                return;
            }

            var remote = client.Client.RemoteEndPoint is IPEndPoint ep ? ep.Address.ToString() : "Red local";
            Log($"Conexión entrante desde {remote}.");
            await RunSessionAsync(conn, msg, remote, ownsConnection: true, ct);
        }
        catch (OperationCanceledException)
        {
            await conn.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log("Error en conexión local: " + ex.Message);
            await conn.DisposeAsync();
        }
    }

    private async Task RunSessionAsync(IConnection conn, ControlMessage connect, string remote, bool ownsConnection, CancellationToken ct)
    {
        try
        {
            var session = await HostSession.TryStartAsync(conn, connect, remote, this, ct);
            if (session is null) return;
            await session.Closed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("Sesión: " + ex.Message);
        }
        finally
        {
            if (ownsConnection)
            {
                try { await conn.DisposeAsync(); } catch { }
            }
        }
    }

    internal bool TryReserveSession()
    {
        lock (_gate)
        {
            if (_sessionBusy) return false;
            _sessionBusy = true;
            return true;
        }
    }

    internal void FailSessionReservation()
    {
        lock (_gate) _sessionBusy = false;
    }

    internal void AttachSession(HostSession session, string description)
    {
        lock (_gate) ActiveSession = session;
        SessionStateChanged?.Invoke(description);
    }

    internal void ReleaseSession(HostSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(ActiveSession, session)) ActiveSession = null;
            _sessionBusy = false;
        }
        SessionStateChanged?.Invoke(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
