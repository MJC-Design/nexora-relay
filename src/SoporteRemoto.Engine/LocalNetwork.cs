using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SoporteRemoto.Engine;

public static class LocalNetwork
{
    public static IReadOnlyList<string> GetLocalIPv4()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var ip = ua.Address.ToString();
                    if (!result.Contains(ip)) result.Add(ip);
                }
            }
        }
        catch
        {
            // Sin información de red disponible.
        }
        return result;
    }
}
