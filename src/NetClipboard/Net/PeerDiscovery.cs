using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetClipboard.Core;

namespace NetClipboard.Net;

/// <summary>
/// Scoperta automatica dei peer via broadcast UDP. Ogni istanza annuncia se
/// stessa ogni pochi secondi; gli annunci sono cifrati con la passphrase
/// condivisa, quindi solo chi ha la password viene "visto".
///
/// L'annuncio viene inviato al broadcast limitato (255.255.255.255) E al
/// broadcast diretto di ogni scheda/subnet, per gestire PC con piu' schede
/// (VPN, macchine virtuali, Wi-Fi + Ethernet).
///
/// Annuncio (dopo decifratura): [magic 'N''C'][ver=1][origin 16][tcpPort:4][nameLen:4][utf8 name]
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    private const byte Version = 1;
    private static readonly byte[] Magic = { (byte)'N', (byte)'C' };
    private const int AnnounceIntervalMs = 3000;
    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(12);

    private readonly AppConfig _config;
    private readonly SecureChannel _channel;
    private readonly ConcurrentDictionary<Guid, Peer> _peers = new();

    private UdpClient? _udp;
    private System.Threading.Timer? _announceTimer;
    private CancellationTokenSource? _cts;

    private bool _loggedSelf;
    private int _announceCount;
    private DateTime _lastFailLogUtc = DateTime.MinValue;
    private string _lastTargets = "";

    public event Action? PeersChanged;

    public PeerDiscovery(AppConfig config, SecureChannel channel)
    {
        _config = config;
        _channel = channel;
    }

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToList();

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
            Log.Write($"[Discovery] BIND FALLITO su UDP {_config.Port}: {ex.Message}");
            udp.Dispose();
            return;
        }
        _udp = udp;

        Log.Write($"[Discovery] avviato · UDP {_config.Port} · IP locali: {string.Join(", ", LocalIPv4())}");

        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _announceTimer = new System.Threading.Timer(_ => Tick(), null, 0, AnnounceIntervalMs);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _announceTimer?.Dispose();
        _announceTimer = null;
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void Tick()
    {
        SendAnnounce();
        ExpirePeers();
    }

    private void SendAnnounce()
    {
        var udp = _udp;
        if (udp == null || !_channel.HasKey)
            return;

        try
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(_config.InstanceId.ToByteArray());
                w.Write(_config.Port);
                var nb = Encoding.UTF8.GetBytes(_config.DisplayName);
                w.Write(nb.Length);
                w.Write(nb);
            }
            var blob = _channel.Encrypt(ms.ToArray());

            var targets = BroadcastTargets();
            var targetStr = string.Join(", ", targets.Select(t => t.ToString()));
            if (targetStr != _lastTargets)
            {
                Log.Write($"[Discovery] destinazioni broadcast: {targetStr}");
                _lastTargets = targetStr;
            }

            foreach (var t in targets)
            {
                try { udp.Send(blob, blob.Length, new IPEndPoint(t, _config.Port)); }
                catch (Exception ex) { Log.Write($"[Discovery] send -> {t} fallito: {ex.Message}"); }
            }

            // Heartbeat ogni ~15s per non intasare il log.
            if (_announceCount++ % 5 == 0)
                Log.Write($"[Discovery] annuncio inviato a {targets.Count} destinazioni (peer noti: {_peers.Count})");
        }
        catch (Exception ex)
        {
            Log.Write($"[Discovery] annuncio fallito: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp;
        if (udp == null)
            return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Write($"[Discovery] receive error: {ex.Message}");
                continue;
            }

            HandleDatagram(res.Buffer, res.RemoteEndPoint.Address);
        }
    }

    private void HandleDatagram(byte[] blob, IPAddress from)
    {
        var plain = _channel.TryDecrypt(blob);
        if (plain == null)
        {
            // Pacchetti che arrivano ma non si decifrano = password diversa o rumore.
            if ((DateTime.UtcNow - _lastFailLogUtc).TotalSeconds > 5)
            {
                _lastFailLogUtc = DateTime.UtcNow;
                Log.Write($"[Discovery] RX da {from}: non decifrabile (password diversa?) len={blob.Length}");
            }
            return;
        }

        try
        {
            using var ms = new MemoryStream(plain);
            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(2);
            if (magic.Length != 2 || magic[0] != Magic[0] || magic[1] != Magic[1])
                return;
            if (r.ReadByte() != Version)
                return;
            var id = new Guid(r.ReadBytes(16));
            var tcpPort = r.ReadInt32();
            var nameLen = r.ReadInt32();
            var name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));

            if (id == _config.InstanceId)
            {
                if (!_loggedSelf)
                {
                    _loggedSelf = true;
                    Log.Write($"[Discovery] RX del proprio annuncio da {from} (invio+ricezione locali OK)");
                }
                return;
            }

            AddOrUpdatePeer(id, name, from, tcpPort, "broadcast");
        }
        catch (Exception ex)
        {
            Log.Write($"[Discovery] parse annuncio: {ex.Message}");
        }
    }

    /// <summary>Registra un peer scoperto per altra via (es. hello TCP manuale).</summary>
    public void ReportPeer(Guid id, string name, IPAddress addr, int port)
    {
        if (id == _config.InstanceId)
            return;
        AddOrUpdatePeer(id, name, addr, port, "TCP");
    }

    private void AddOrUpdatePeer(Guid id, string name, IPAddress addr, int port, string source)
    {
        var isNew = !_peers.ContainsKey(id);
        _peers.AddOrUpdate(id,
            _ => new Peer { Id = id, Name = name, Address = addr, TcpPort = port },
            (_, existing) =>
            {
                existing.Name = name;
                existing.Address = addr;
                existing.TcpPort = port;
                existing.LastSeenUtc = DateTime.UtcNow;
                return existing;
            });

        if (isNew)
        {
            Log.Write($"[Discovery] PEER TROVATO ({source}): {name} @ {addr}:{port}");
            PeersChanged?.Invoke();
        }
    }

    private void ExpirePeers()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var kv in _peers)
        {
            if (now - kv.Value.LastSeenUtc > PeerTtl)
            {
                if (_peers.TryRemove(kv.Key, out var p))
                {
                    removed = true;
                    Log.Write($"[Discovery] peer scaduto: {p.Name} @ {p.Address}");
                }
            }
        }
        if (removed)
            PeersChanged?.Invoke();
    }

    // ----- Broadcast targets -----

    private List<IPAddress> BroadcastTargets()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast }; // 255.255.255.255
        foreach (var b in DirectedBroadcasts())
            if (!list.Contains(b))
                list.Add(b);
        return list;
    }

    private static IEnumerable<IPAddress> DirectedBroadcasts()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = ua.IPv4Mask;
                if (mask == null) continue;
                var ipBytes = ua.Address.GetAddressBytes();
                var maskBytes = mask.GetAddressBytes();
                if (maskBytes.Length != 4 || (maskBytes[0] == 0 && maskBytes[1] == 0)) continue;
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    bcast[i] = (byte)(ipBytes[i] | (~maskBytes[i] & 0xFF));
                yield return new IPAddress(bcast);
            }
        }
    }

    private static IEnumerable<string> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return $"{ua.Address} ({ni.Name})";
        }
    }

    public void Dispose() => Stop();
}
