using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetClipboard.Net;

/// <summary>
/// Enumera gli host IPv4 delle sottoreti locali, per la scoperta tramite
/// scansione attiva TCP (quando il broadcast è filtrato dalla rete).
/// </summary>
public static class NetworkScan
{
    /// <summary>
    /// Tutti gli indirizzi host delle subnet locali (escl. rete, broadcast e sé stessi).
    /// Salta le subnet più grandi di <paramref name="maxHostsPerSubnet"/> per non
    /// generare scansioni enormi (es. /16).
    /// </summary>
    public static List<IPAddress> LocalSubnetHosts(int maxHostsPerSubnet)
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<uint>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask;
                if (mask == null) continue;

                var ip = ToUInt(ua.Address.GetAddressBytes());
                var m = ToUInt(mask.GetAddressBytes());
                if (m == 0) continue;

                var hostBits = 32 - PopCount(m);
                if (hostBits <= 1) continue; // /31, /32
                long count = (1L << hostBits) - 2;
                if (count > maxHostsPerSubnet) continue;

                var network = ip & m;
                var broadcast = network | ~m;

                for (var h = network + 1; h < broadcast; h++)
                {
                    if (h == ip) continue;      // noi stessi
                    if (!seen.Add(h)) continue; // già presente da un'altra scheda
                    result.Add(FromUInt(h));
                }
            }
        }
        return result;
    }

    private static uint ToUInt(byte[] b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    private static int PopCount(uint v)
    {
        var c = 0;
        while (v != 0) { c += (int)(v & 1); v >>= 1; }
        return c;
    }
}
