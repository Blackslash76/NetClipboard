using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetClipboard.Core;

namespace NetClipboard.Net;

/// <summary>Payload ricevuto in push da un peer, con chi l'ha inviato.</summary>
public sealed record ReceivedClip(ClipboardPayload Payload, string FromName, Guid FromId);

/// <summary>Identita' di un peer scoperto (via hello TCP manuale).</summary>
public sealed record PeerInfo(Guid Id, string Name, IPAddress Address, int Port);

/// <summary>
/// Trasporto TCP. Due tipi di scambio:
///   - PUSH: testo/immagine/offer inviati in broadcast ai peer (una connessione, un messaggio).
///   - FETCH: il destinatario chiede i byte di un'offerta file; l'host li STREAMA
///     a chunk cifrati (delayed rendering: i byte partono solo su incolla).
///
/// Frame sul filo: [len:4 big-endian][blob cifrato].
/// Envelope (dopo decifratura): [magic 'N''C'][ver=1][origin 16][msgType 1][body].
///   msgType 1 = Push  -> body = ClipboardPayload.Serialize()
///   msgType 2 = Fetch -> body = offerId (16 byte)
/// Nella risposta di fetch, ogni frame decifra a: [frameType 1][...].
/// </summary>
public sealed class ClipboardTransport : IDisposable
{
    private const byte Version = 1;
    private static readonly byte[] Magic = { (byte)'N', (byte)'C' };

    private const byte MsgPush = 1;
    private const byte MsgFetch = 2;
    private const byte MsgHello = 3;

    private const byte FrameEnd = 0x00;
    private const byte FrameEntryHeader = 0x01;
    private const byte FrameData = 0x02;
    private const byte FrameEntryEnd = 0x03;
    private const byte FrameError = 0x7F;

    private const int ChunkSize = 64 * 1024;

    private readonly AppConfig _config;
    private readonly SecureChannel _channel;
    private readonly OfferStore _offerStore;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _pingTimer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _activePeerIps = new();
    private readonly Lock _cacheGate = new();

    public event Action<ReceivedClip>? Received;
    public event Action<PeerInfo>? PeerSeen;

    /// <summary>Fornisce i peer attualmente noti, per il gossip negli hello.</summary>
    public Func<IEnumerable<PeerInfo>>? KnownPeersProvider;

    public ClipboardTransport(AppConfig config, SecureChannel channel, OfferStore offerStore)
    {
        _config = config;
        _channel = channel;
        _offerStore = offerStore;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start();
            Log.Write($"[Transport] in ascolto su TCP {_config.Port}");
        }
        catch (Exception ex)
        {
            Log.Write($"[Transport] LISTEN FALLITO su TCP {_config.Port}: {ex.Message}");
            return;
        }
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        // Semina dai peer già noti (cache): al riavvio si ripingano, niente scan.
        foreach (var ip in _config.KnownPeerIps)
            _activePeerIps.TryAdd(ip, 1);
        if (_config.ManualPeers.Count > 0)
            Log.Write($"[Transport] peer manuali: {string.Join(", ", _config.ManualPeers)}");
        if (!_activePeerIps.IsEmpty)
            Log.Write($"[Transport] bootstrap da cache: {_activePeerIps.Count} IP noti");

        // Keep-alive TCP verso i peer noti (cache + manuali + scoperti via gossip) ogni 3s.
        _pingTimer = new System.Threading.Timer(_ => PingKnown(), null, 500, 3000);

        // Scansione SOLO alla prima configurazione (nessun peer noto). Poi: gossip + on-demand.
        if (_config.AutoScanDiscovery && _activePeerIps.IsEmpty && _config.ManualPeers.Count == 0)
        {
            Log.Write("[Transport] prima configurazione: scansione di bootstrap");
            _ = Task.Run(() => ScanAsync(_cts.Token, "bootstrap"));
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pingTimer?.Dispose();
        _pingTimer = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    // ===================== LATO SERVER =====================

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Debug.WriteLine($"[Transport] accept: {ex.Message}"); continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 30_000;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                await using var stream = client.GetStream();

                var blob = await ReadFrameAsync(stream, ct);
                if (blob == null) return;
                var plain = _channel.TryDecrypt(blob);
                if (plain == null) return;

                if (!TryParseEnvelope(plain, out var originId, out var msgType, out var body))
                    return;
                if (originId == _config.InstanceId)
                    return;

                if (msgType == MsgPush)
                {
                    var payload = ClipboardPayload.Deserialize(body);
                    Received?.Invoke(new ReceivedClip(payload, originId.ToString(), originId));
                }
                else if (msgType == MsgFetch && body.Length >= 16)
                {
                    var offerId = new Guid(body.AsSpan(0, 16).ToArray());
                    await ServeFetchAsync(stream, offerId, ct);
                }
                else if (msgType == MsgHello)
                {
                    var (port, name, gossip) = ParseHello(body);
                    // Rispondiamo col nostro hello (che porta la nostra lista peer) e poi elaboriamo.
                    var reply = BuildFrame(MsgHello, HelloBody());
                    await stream.WriteAsync(reply, ct);
                    await stream.FlushAsync(ct);
                    ProcessHello(originId, name, remote, port, gossip);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Transport] handle client: {ex.Message}");
            }
        }
    }

    private async Task ServeFetchAsync(NetworkStream stream, Guid offerId, CancellationToken ct)
    {
        var offer = _offerStore.Get(offerId);
        if (offer == null || offer.RootParents == null)
        {
            await WriteFrameAsync(stream, ErrorFrame("Offerta non piu' disponibile sull'host."), ct);
            return;
        }

        foreach (var entry in offer.Entries)
        {
            if (ct.IsCancellationRequested) return;

            var abs = offer.ResolveLocal(entry);
            if (entry.IsDir)
            {
                await WriteFrameAsync(stream, HeaderFrame(entry, 0), ct);
                await WriteFrameAsync(stream, new[] { FrameEntryEnd }, ct);
                continue;
            }

            if (abs == null || !File.Exists(abs))
                continue; // file sparito: lo saltiamo

            long size;
            try { size = new FileInfo(abs).Length; }
            catch { continue; }

            await WriteFrameAsync(stream, HeaderFrame(entry, size), ct);
            try
            {
                await using var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buffer = new byte[ChunkSize];
                int read;
                while ((read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize), ct)) > 0)
                {
                    var frame = new byte[read + 1];
                    frame[0] = FrameData;
                    Buffer.BlockCopy(buffer, 0, frame, 1, read);
                    await WriteFrameAsync(stream, frame, ct);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Transport] lettura {abs}: {ex.Message}");
            }
            await WriteFrameAsync(stream, new[] { FrameEntryEnd }, ct);
        }

        await WriteFrameAsync(stream, new[] { FrameEnd }, ct);
    }

    private static byte[] HeaderFrame(FileEntry e, long size)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(FrameEntryHeader);
        w.Write(e.IsDir);
        w.Write(size);
        var rel = Encoding.UTF8.GetBytes(e.RelativePath);
        w.Write(rel.Length);
        w.Write(rel);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] ErrorFrame(string msg)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(FrameError);
        var b = Encoding.UTF8.GetBytes(msg);
        w.Write(b.Length);
        w.Write(b);
        w.Flush();
        return ms.ToArray();
    }

    // ===================== LATO CLIENT =====================

    /// <summary>Invia il payload (push) a tutti i peer indicati. Best effort.</summary>
    public async Task SendAsync(ClipboardPayload payload, IReadOnlyCollection<Peer> peers)
    {
        if (!_channel.HasKey || peers.Count == 0) return;
        var frame = BuildFrame(MsgPush, payload.Serialize());
        await Task.WhenAll(peers.Select(p => SendFrameToPeerAsync(p, frame)));
    }

    private static async Task SendFrameToPeerAsync(Peer peer, byte[] frame)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(peer.Address, peer.TcpPort, cts.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(frame);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Transport] invio a {peer}: {ex.Message}");
        }
    }

    /// <summary>Contatta un singolo indirizzo con un hello (probe on-demand).</summary>
    public Task ProbeAsync(IPAddress addr, CancellationToken ct = default) => HelloAsync(addr, ct, 2500);

    /// <summary>Scansione della subnet su richiesta dell'utente.</summary>
    public void ScanOnDemand() =>
        _ = Task.Run(() => ScanAsync(_cts?.Token ?? CancellationToken.None, "on-demand"));

    // ----- Scoperta / keep-alive via TCP -----

    /// <summary>Keep-alive verso i peer noti (cache + manuali + scoperti via gossip).</summary>
    private void PingKnown()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        var targets = new HashSet<string>(_activePeerIps.Keys);
        foreach (var m in _config.ManualPeers)
            targets.Add(m.Trim());

        foreach (var raw in targets)
            if (IPAddress.TryParse(raw, out var addr))
                _ = Task.Run(() => HelloAsync(addr, ct, 3000), ct);
    }

    private async Task ScanAsync(CancellationToken ct, string reason)
    {
        List<IPAddress> hosts;
        try { hosts = NetworkScan.LocalSubnetHosts(2048); }
        catch { return; }

        if (hosts.Count == 0)
        {
            Log.Write("[Transport] scansione saltata: subnet assente o troppo grande");
            return;
        }

        Log.Write($"[Transport] scansione ({reason}): {hosts.Count} host sulla porta {_config.Port}");
        try
        {
            await Parallel.ForEachAsync(hosts,
                new ParallelOptions { MaxDegreeOfParallelism = 100, CancellationToken = ct },
                async (ip, token) => await HelloAsync(ip, token, 700));
        }
        catch (OperationCanceledException) { }
    }

    private async Task HelloAsync(IPAddress addr, CancellationToken ct, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            await client.ConnectAsync(addr, _config.Port, cts.Token);
            await using var stream = client.GetStream();

            await stream.WriteAsync(BuildFrame(MsgHello, HelloBody()), cts.Token);
            await stream.FlushAsync(cts.Token);

            var blob = await ReadFrameAsync(stream, cts.Token);
            if (blob == null) return;
            var plain = _channel.TryDecrypt(blob);
            if (plain == null) return;
            if (TryParseEnvelope(plain, out var originId, out var msgType, out var body)
                && msgType == MsgHello && originId != _config.InstanceId)
            {
                var (port, name, gossip) = ParseHello(body);
                ProcessHello(originId, name, addr, port, gossip);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Transport] hello -> {addr}: {ex.Message}");
        }
    }

    /// <summary>
    /// Registra il mittente e i peer che ci ha "raccontato": è il gossip che rende
    /// la mesh auto-assemblante — agganciandone uno, si scoprono tutti gli altri.
    /// </summary>
    private void ProcessHello(Guid senderId, string senderName, IPAddress senderAddr, int senderPort, List<PeerInfo> gossip)
    {
        if (!senderAddr.Equals(IPAddress.None))
            RememberPeerIp(senderAddr.ToString());
        PeerSeen?.Invoke(new PeerInfo(senderId, senderName, senderAddr, senderPort));

        foreach (var gp in gossip)
        {
            if (gp.Id == _config.InstanceId || gp.Id == senderId) continue;
            if (gp.Address.Equals(IPAddress.None)) continue;
            RememberPeerIp(gp.Address.ToString());
            PeerSeen?.Invoke(gp);
        }
    }

    private void RememberPeerIp(string ip)
    {
        if (!_activePeerIps.TryAdd(ip, 1))
            return; // già noto: nessuna riscrittura della cache
        lock (_cacheGate)
        {
            if (!_config.KnownPeerIps.Contains(ip))
            {
                _config.KnownPeerIps.Add(ip);
                if (_config.KnownPeerIps.Count > 64)
                    _config.KnownPeerIps.RemoveAt(0);
                _config.Save();
            }
        }
    }

    private byte[] HelloBody()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(_config.Port);
        WriteStr(w, _config.DisplayName);

        var peers = (KnownPeersProvider?.Invoke() ?? Enumerable.Empty<PeerInfo>())
            .Where(p => p.Id != _config.InstanceId)
            .Take(64)
            .ToList();
        w.Write(peers.Count);
        foreach (var p in peers)
        {
            w.Write(p.Id.ToByteArray());
            w.Write(p.Port);
            WriteStr(w, p.Name);
            WriteStr(w, p.Address.ToString());
        }
        w.Flush();
        return ms.ToArray();
    }

    private static (int Port, string Name, List<PeerInfo> Gossip) ParseHello(byte[] body)
    {
        var gossip = new List<PeerInfo>();
        try
        {
            using var ms = new MemoryStream(body);
            using var r = new BinaryReader(ms);
            var port = r.ReadInt32();
            var name = ReadStr(r);
            if (ms.Position < ms.Length) // lista peer opzionale
            {
                var count = r.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var id = new Guid(r.ReadBytes(16));
                    var pPort = r.ReadInt32();
                    var pName = ReadStr(r);
                    var pIp = ReadStr(r);
                    if (IPAddress.TryParse(pIp, out var addr))
                        gossip.Add(new PeerInfo(id, pName, addr, pPort));
                }
            }
            return (port, name, gossip);
        }
        catch
        {
            return (0, "?", gossip);
        }
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
    }

    private static string ReadStr(BinaryReader r)
    {
        var len = r.ReadInt32();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    /// <summary>
    /// Scarica i byte di un'offerta dall'host e li scrive sotto destDir,
    /// ricreando l'albero di cartelle. Ritorna i percorsi radice materializzati.
    /// </summary>
    public async Task<List<string>> FetchAsync(Peer owner, Guid offerId, string destDir, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var requestFrame = BuildFrame(MsgFetch, offerId.ToByteArray());

        using var client = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(owner.Address, owner.TcpPort, connectCts.Token);
        client.ReceiveTimeout = 60_000;

        await using var stream = client.GetStream();
        await stream.WriteAsync(requestFrame, ct);
        await stream.FlushAsync(ct);

        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileStream? current = null;

        try
        {
            while (true)
            {
                var blob = await ReadFrameAsync(stream, ct);
                if (blob == null) break; // connessione chiusa
                var plain = _channel.TryDecrypt(blob);
                if (plain == null) break;

                var type = plain[0];
                if (type == FrameEnd)
                    break;
                if (type == FrameError)
                {
                    var msg = Encoding.UTF8.GetString(plain, 5, ReadInt(plain, 1));
                    throw new IOException(msg);
                }
                if (type == FrameEntryHeader)
                {
                    current?.Dispose();
                    current = null;

                    var isDir = plain[1] != 0;
                    var relLen = ReadInt(plain, 10); // 1 type +1 isDir +8 size
                    var rel = Encoding.UTF8.GetString(plain, 14, relLen);
                    if (!rel.Contains('/'))
                        topNames.Add(rel);

                    var localRel = rel.Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.Combine(destDir, localRel);
                    if (isDir)
                    {
                        Directory.CreateDirectory(target);
                    }
                    else
                    {
                        var parent = Path.GetDirectoryName(target);
                        if (parent != null) Directory.CreateDirectory(parent);
                        current = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    }
                }
                else if (type == FrameData)
                {
                    if (current != null)
                        await current.WriteAsync(plain.AsMemory(1), ct);
                }
                else if (type == FrameEntryEnd)
                {
                    current?.Dispose();
                    current = null;
                }
            }
        }
        finally
        {
            current?.Dispose();
        }

        return topNames
            .Select(n => Path.Combine(destDir, n))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
    }

    // ===================== FRAMING / ENVELOPE =====================

    private byte[] BuildFrame(byte msgType, byte[] body)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(_config.InstanceId.ToByteArray());
            w.Write(msgType);
            w.Write(body);
        }
        var blob = _channel.Encrypt(ms.ToArray());
        var frame = new byte[4 + blob.Length];
        WriteInt32BigEndian(frame, blob.Length);
        Buffer.BlockCopy(blob, 0, frame, 4, blob.Length);
        return frame;
    }

    private static bool TryParseEnvelope(byte[] plain, out Guid originId, out byte msgType, out byte[] body)
    {
        originId = Guid.Empty;
        msgType = 0;
        body = Array.Empty<byte>();
        try
        {
            using var ms = new MemoryStream(plain);
            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(2);
            if (magic.Length != 2 || magic[0] != Magic[0] || magic[1] != Magic[1]) return false;
            if (r.ReadByte() != Version) return false;
            originId = new Guid(r.ReadBytes(16));
            msgType = r.ReadByte();
            body = r.ReadBytes((int)(ms.Length - ms.Position));
            return true;
        }
        catch { return false; }
    }

    private async Task WriteFrameAsync(NetworkStream stream, byte[] plaintext, CancellationToken ct)
    {
        var blob = _channel.Encrypt(plaintext);
        var frame = new byte[4 + blob.Length];
        WriteInt32BigEndian(frame, blob.Length);
        Buffer.BlockCopy(blob, 0, frame, 4, blob.Length);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = await ReadExactAsync(stream, 4, ct);
        if (lenBuf == null) return null;
        var len = BinaryPrimitivesReadInt32BigEndian(lenBuf);
        var maxBytes = _config.MaxTransferMb * 1024L * 1024L + (2 << 20);
        if (len <= 0 || len > maxBytes) return null;
        return await ReadExactAsync(stream, len, ct);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    private static int ReadInt(byte[] b, int offset) =>
        b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

    private static int BinaryPrimitivesReadInt32BigEndian(byte[] b) =>
        (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];

    private static void WriteInt32BigEndian(byte[] target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    public void Dispose() => Stop();
}
