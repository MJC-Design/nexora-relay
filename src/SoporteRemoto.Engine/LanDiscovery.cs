using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SoporteRemoto.Protocol;

namespace SoporteRemoto.Engine;

public sealed record LanEndpoint(string Address, int Port, string MachineName);

/// <summary>Responde a las búsquedas UDP de la red local cuando el código coincide.</summary>
public sealed class LanDiscoveryHost : IDisposable
{
    private const string RequestPrefix = "SRLAN1?";
    private const string ReplyPrefix = "SRLAN1!";

    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly Func<string> _codeProvider;
    private readonly int _tcpPort;
    private readonly CancellationTokenSource _cts = new();

    public LanDiscoveryHost(Func<string> codeProvider, int tcpPort)
    {
        _codeProvider = codeProvider;
        _tcpPort = tcpPort;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.ExclusiveAddressUse = false;
        _udp.EnableBroadcast = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, ProtocolConstants.DiscoveryPort));
    }

    public void Start() => _ = LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await _udp.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(packet.Buffer);
                if (!text.StartsWith(RequestPrefix, StringComparison.Ordinal)) continue;

                var code = SessionCode.Normalize(text[RequestPrefix.Length..]);
                if (code.Length != SessionCode.Length || !string.Equals(code, _codeProvider(), StringComparison.Ordinal))
                    continue;

                var reply = Encoding.UTF8.GetBytes(
                    $"{ReplyPrefix}{code}|{_tcpPort}|{DeviceInfo.Name}");
                try { await _udp.SendAsync(reply, packet.RemoteEndPoint, ct); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _udp.Dispose(); } catch { }
        _cts.Dispose();
    }
}

public static class LanDiscovery
{
    private const string RequestPrefix = "SRLAN1?";
    private const string ReplyPrefix = "SRLAN1!";

    /// <summary>Busca en la red local el equipo que usa ese código. Devuelve null si no responde.</summary>
    public static async Task<LanEndpoint?> FindAsync(
        string code, TimeSpan timeout, CancellationToken ct = default)
    {
        var normalized = SessionCode.Normalize(code);
        if (normalized.Length != SessionCode.Length) return null;

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var request = Encoding.UTF8.GetBytes(RequestPrefix + normalized);
        var targets = GetTargets();
        var receive = ReceiveReplyAsync(udp, normalized, cts.Token);
        try
        {
            while (true)
            {
                foreach (var target in targets)
                {
                    try { await udp.SendAsync(request, target, cts.Token); }
                    catch (SocketException) { }
                }

                var delay = Task.Delay(350, cts.Token);
                var winner = await Task.WhenAny(receive, delay);
                if (winner == receive)
                {
                    var endpoint = await receive;
                    if (endpoint is not null) return endpoint;
                    receive = ReceiveReplyAsync(udp, normalized, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try { cts.Cancel(); } catch { }
            try { await receive; } catch { }
        }
    }

    private static async Task<LanEndpoint?> ReceiveReplyAsync(
        UdpClient udp, string code, CancellationToken ct)
    {
        while (true)
        {
            var packet = await udp.ReceiveAsync(ct);
            var text = Encoding.UTF8.GetString(packet.Buffer);
            if (!text.StartsWith(ReplyPrefix, StringComparison.Ordinal)) continue;

            var parts = text[ReplyPrefix.Length..].Split('|');
            if (parts.Length < 2) continue;
            if (!string.Equals(parts[0], code, StringComparison.Ordinal)) continue;
            if (!int.TryParse(parts[1], out var port) || port <= 0) continue;

            var machine = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : "Equipo remoto";
            return new LanEndpoint(packet.RemoteEndPoint.Address.ToString(), port, machine);
        }
    }

    private static List<IPEndPoint> GetTargets()
    {
        var targets = new List<IPEndPoint>
        {
            new(IPAddress.Broadcast, ProtocolConstants.DiscoveryPort),
            new(IPAddress.Loopback, ProtocolConstants.DiscoveryPort),
        };

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var mask = ua.IPv4Mask;
                    if (mask is null) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var m = mask.GetAddressBytes();
                    if (ip.Length != 4 || m.Length != 4) continue;

                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~m[i]);
                    var address = new IPAddress(broadcast);
                    if (!targets.Exists(t => t.Address.Equals(address)))
                        targets.Add(new IPEndPoint(address, ProtocolConstants.DiscoveryPort));
                }
            }
        }
        catch
        {
            // Sin información de interfaces: queda la difusión genérica.
        }

        return targets;
    }
}
