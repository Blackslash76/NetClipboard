using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetClipboard.Core;

namespace NetClipboard.Net;

/// <summary>
/// Scoperta di presenza via broadcast UDP (in chiaro): serve solo a segnalare
/// "qui c'è un NetClipboard, prova a contattarmi". L'identità e la fiducia sono
/// stabilite dall'handshake TCP autenticato del trasporto. Ogni IP da cui arriva
/// un annuncio viene passato al trasporto come candidato da contattare.
///
/// Annuncio: [magic 'N''C'][ver=2][tcpPort:4]
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    private const byte Version = 2;
    private static readonly byte[] Magic = { (byte)'N', (byte)'C' };
    private const int AnnounceIntervalMs = 3000;

    private readonly AppConfig _config;
    private readonly Action<IPAddress> _onCandidate;
    private readonly HashSet<string> _localIps;

    private UdpClient? _udp;
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _cts;
    private string _lastTargets = "";

    public PeerDiscovery(AppConfig config, Action<IPAddress> onCandidate)
    {
        _config = config;
        _onCandidate = onCandidate;
        _localIps = LocalIPv4().ToHashSet();
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();

        var udp = new UdpClient { EnableBroadcast = true };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.ExclusiveAddressUse = false;
        try
        {
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _config.Port));
        }
        catch (Exception ex)
        {
            Log.Write($"[Discovery] bind UDP {_config.Port} fallito: {ex.Message}");
            udp.Dispose();
            return;
        }
        _udp = udp;
        Log.Write($"[Discovery] avviato · UDP {_config.Port} · IP locali: {string.Join(", ", _localIps)}");

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _timer = new System.Threading.Timer(_ => SendAnnounce(), null, 0, AnnounceIntervalMs);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose(); _timer = null;
        _udp?.Dispose(); _udp = null;
        _cts?.Dispose(); _cts = null;
    }

    private void SendAnnounce()
    {
        var udp = _udp;
        if (udp == null) return;
        try
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(_config.Port);
            }
            var data = ms.ToArray();
            var targets = BroadcastTargets();
            var ts = string.Join(", ", targets.Select(t => t.ToString()));
            if (ts != _lastTargets) { Log.Write($"[Discovery] broadcast verso: {ts}"); _lastTargets = ts; }
            foreach (var t in targets)
            {
                try { udp.Send(data, data.Length, new IPEndPoint(t, _config.Port)); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Discovery] annuncio fallito: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp;
        if (udp == null) return;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            var b = res.Buffer;
            if (b.Length >= 3 && b[0] == Magic[0] && b[1] == Magic[1] && b[2] == Version)
            {
                var ip = res.RemoteEndPoint.Address;
                if (!_localIps.Contains(ip.ToString()))
                    _onCandidate(ip);
            }
        }
    }

    private static List<IPAddress> BroadcastTargets()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask;
                if (mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mk = mask.GetAddressBytes();
                if (mk.Length != 4 || (mk[0] == 0 && mk[1] == 0)) continue;
                var bc = new byte[4];
                for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | (~mk[i] & 0xFF));
                var addr = new IPAddress(bc);
                if (!list.Contains(addr)) list.Add(addr);
            }
        }
        return list;
    }

    private static IEnumerable<string> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return ua.Address.ToString();
        }
    }

    public void Dispose() => Stop();
}
