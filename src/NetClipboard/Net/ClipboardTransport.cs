using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetClipboard.Core;
using NetClipboard.Core.Identity;
using NetClipboard.Core.Security;

namespace NetClipboard.Net;

public sealed record ReceivedClip(ClipboardPayload Payload, string FromName, string FromDeviceId);

/// <summary>Dati mostrati all'utente per confermare un pairing (codice + chi).</summary>
public sealed record PairingPrompt(string Sas, string PeerName, string Fingerprint);

public enum PairOutcome { Paired, Rejected, Failed }

/// <summary>Richiesta di invio in arrivo da un peer NON accoppiato: va confermata a mano.</summary>
public sealed record IncomingOffer(string FromLabel, string FromDeviceId, PayloadKind Kind, string Preview);

public enum SendOutcome { Delivered, Declined, Failed }

/// <summary>Avanzamento del download dei file (delayed rendering).</summary>
public sealed record FetchProgress(string CurrentName, long BytesDone, int FilesDone);

/// <summary>
/// Trasporto TCP sicuro. OGNI connessione inizia con un handshake autenticato
/// (identità per-dispositivo, forward secrecy) che stabilisce una chiave di
/// sessione e un codice SAS; poi i messaggi viaggiano cifrati con quella chiave.
///
/// Operazioni (primo frame di sessione): Ping (presenza + gossip), Push (clipboard),
/// Fetch (file on-demand), Pair (accoppiamento con conferma del codice).
/// Push/Fetch avvengono SOLO tra dispositivi fidati (chiave pinnata). Il gossip su
/// sessione fidata introduce i peer del peer → la mesh si forma da sola.
/// </summary>
public sealed class ClipboardTransport : IDisposable
{
    private static readonly byte[] Magic = { (byte)'N', (byte)'C' };
    private const byte Version = 2;

    private const byte OpPing = 1;
    private const byte OpPush = 2;
    private const byte OpFetch = 3;
    private const byte OpPair = 4;
    private const byte OpOffer = 5;   // invio mirato a un peer non accoppiato (richiede conferma)

    // Frame di streaming fetch (dentro la sessione cifrata)
    private const byte FEnd = 0x00;
    private const byte FHeader = 0x01;
    private const byte FData = 0x02;
    private const byte FEntryEnd = 0x03;
    private const byte FError = 0x7F;
    private const int ChunkSize = 64 * 1024;

    private static readonly TimeSpan PeerTtl = TimeSpan.FromSeconds(15);

    private readonly AppConfig _config;
    private readonly DeviceIdentity _identity;
    private readonly TrustStore _trust;
    private readonly OfferStore _offerStore;

    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<string, byte> _activeIps = new();
    private readonly Lock _cacheGate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _pingTimer;

    public event Action<ReceivedClip>? Received;
    public event Action? PeersChanged;

    /// <summary>Chiamato per confermare un pairing mostrando il codice SAS; ritorna true se accettato.</summary>
    public Func<PairingPrompt, bool>? PairingConfirm;

    /// <summary>Chiamato quando un peer NON accoppiato ci manda qualcosa; ritorna true se l'utente accetta.</summary>
    public Func<IncomingOffer, bool>? OfferConfirm;

    /// <summary>
    /// Identità aziendale di chi usa questo PC, annunciata nel ping perché gli
    /// altri possano elencarci per nome. Null finché non c'è un accesso Entra.
    /// </summary>
    public WorkIdentity? SelfWork { get; set; }

    /// <summary>
    /// Permessi di prelievo file concessi uno per uno: "il dispositivo X può
    /// scaricare l'offerta Y". Servono perché un invio a un non accoppiato
    /// possa contenere file senza aprire OpFetch a chiunque.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _fetchGrants = new();

    /// <summary>
    /// L'altra faccia di <see cref="_fetchGrants"/>: le offerte che NOI abbiamo
    /// accettato da un peer non accoppiato. Senza questo elenco il prelievo dei
    /// file si fermerebbe, perché FetchAsync pretende un peer fidato.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _acceptedOffers = new();

    /// <summary>Ultima richiesta di invio per peer, per non farsi tempestare di finestre.</summary>
    private readonly ConcurrentDictionary<string, long> _lastOfferAt = new();

    private const int OfferCooldownMs = 10_000;

    /// <summary>0/1: una sola finestra di conferma per volta, qualunque sia il mittente.</summary>
    private int _offerDialogOpen;

    public ClipboardTransport(AppConfig config, DeviceIdentity identity, TrustStore trust, OfferStore offerStore)
    {
        _config = config;
        _identity = identity;
        _trust = trust;
        _offerStore = offerStore;
    }

    public IReadOnlyCollection<Peer> Peers => _peers.Values.ToList();
    public IReadOnlyCollection<Peer> TrustedPeers => _peers.Values.Where(p => p.Trusted).ToList();
    public int Port => _config.Port;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start();
            Log.Write($"[Transport] in ascolto su TCP {_config.Port} · deviceId {DeviceIdentity.ShortFingerprint(_identity.DeviceId)}");
        }
        catch (Exception ex)
        {
            Log.Write($"[Transport] LISTEN FALLITO su {_config.Port}: {ex.Message}");
            return;
        }

        foreach (var ip in _config.KnownPeerIps) _activeIps.TryAdd(ip, 1);
        foreach (var m in _config.ManualPeers) _activeIps.TryAdd(m.Trim(), 1);

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _pingTimer = new System.Threading.Timer(_ => PingKnown(), null, 500, 3000);

        if (_config.AutoScanDiscovery && _config.KnownPeerIps.Count == 0 && _config.ManualPeers.Count == 0)
        {
            Log.Write("[Transport] prima configurazione: scansione di bootstrap");
            _ = Task.Run(() => ScanAsync(_cts.Token, "bootstrap"));
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pingTimer?.Dispose(); _pingTimer = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose(); _cts = null;
    }

    public void AddCandidate(IPAddress ip) => _activeIps.TryAdd(ip.ToString(), 1);

    public void ScanOnDemand() => _ = Task.Run(() => ScanAsync(_cts?.Token ?? CancellationToken.None, "on-demand"));

    // ===================== SERVER =====================

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
            catch { continue; }
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 120_000;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                await using var stream = client.GetStream();

                var session = await ServerHandshakeAsync(stream, ct);
                if (session == null) return;
                if (session.R.PeerDeviceId == _identity.DeviceId) return; // noi stessi

                var trusted = _trust.Matches(session.R.PeerDeviceId, session.R.PeerPublicKeyDer);

                var first = await ReadSessionAsync(stream, session.Cipher, ct);
                if (first == null || first.Length == 0) return;
                var op = first[0];
                var body = first[1..];

                switch (op)
                {
                    case OpPing:
                        await HandlePingServerAsync(stream, session, remote, trusted, body, ct);
                        break;
                    case OpPush when trusted:
                        var payload = ClipboardPayload.Deserialize(body);
                        Received?.Invoke(new ReceivedClip(payload, PeerName(session.R.PeerDeviceId), session.R.PeerDeviceId));
                        break;
                    case OpFetch:
                        var offerId = new Guid(body.AsSpan(0, 16).ToArray());
                        if (trusted || IsGranted(session.R.PeerDeviceId, offerId))
                            await ServeFetchAsync(stream, session, offerId, ct);
                        break;
                    case OpOffer:
                        await HandleOfferServerAsync(stream, session, body, ct);
                        break;
                    case OpPair:
                        await HandlePairServerAsync(stream, session, remote, body, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Transport] serve: {ex.Message}");
            }
        }
    }

    private async Task HandlePingServerAsync(NetworkStream stream, Session s, IPAddress remote, bool trusted, byte[] body, CancellationToken ct)
    {
        var (name, port, gossip, work) = ParsePing(body);
        UpsertPeer(s.R.PeerDeviceId, name, remote, port, s.R.PeerPublicKeyDer, trusted, work);
        if (trusted) ProcessGossip(gossip);
        await WriteSessionAsync(stream, s.Cipher, BuildPing(), ct);
    }

    /// <summary>
    /// Invio mirato da un peer non accoppiato: si chiede conferma all'utente e si
    /// risponde subito sì/no, così il mittente sa se è stato consegnato.
    /// </summary>
    private async Task HandleOfferServerAsync(NetworkStream stream, Session s, byte[] body, CancellationToken ct)
    {
        ClipboardPayload payload;
        try { payload = ClipboardPayload.Deserialize(body); }
        catch { return; }

        var peer = _peers.GetValueOrDefault(s.R.PeerDeviceId);
        var label = peer?.Label ?? DeviceIdentity.ShortFingerprint(s.R.PeerDeviceId);

        // Chi non è accoppiato non deve poter far comparire finestre a raffica:
        // una richiesta ogni OfferCooldownMs per mittente, e mai due insieme.
        var now = Environment.TickCount64;
        var last = _lastOfferAt.GetValueOrDefault(s.R.PeerDeviceId);
        if (last != 0 && now - last < OfferCooldownMs)
        {
            Log.Write($"[Transport] invio da {label} ignorato: troppo ravvicinato");
            await WriteSessionAsync(stream, s.Cipher, new byte[] { 0 }, ct);
            return;
        }
        _lastOfferAt[s.R.PeerDeviceId] = now;

        if (Interlocked.CompareExchange(ref _offerDialogOpen, 1, 0) != 0)
        {
            Log.Write($"[Transport] invio da {label} ignorato: c'è già una richiesta aperta");
            await WriteSessionAsync(stream, s.Cipher, new byte[] { 0 }, ct);
            return;
        }

        bool accepted;
        try
        {
            accepted = OfferConfirm?.Invoke(
                new IncomingOffer(label, s.R.PeerDeviceId, payload.Kind, payload.ShortPreview())) ?? false;
        }
        finally { Interlocked.Exchange(ref _offerDialogOpen, 0); }

        // Se sono file, il contenuto non è ancora arrivato: va annotato il permesso
        // di andarselo a prendere, altrimenti il prelievo verrebbe rifiutato.
        if (accepted && payload.Kind == PayloadKind.Files && payload.Offer != null)
            _acceptedOffers.TryAdd(GrantKey(s.R.PeerDeviceId, payload.Offer.OfferId), 1);

        await WriteSessionAsync(stream, s.Cipher, new[] { accepted ? (byte)1 : (byte)0 }, ct);
        Log.Write($"[Transport] invio da {label}: {(accepted ? "accettato" : "rifiutato")}");
        if (accepted) Received?.Invoke(new ReceivedClip(payload, label, s.R.PeerDeviceId));
    }

    private static string GrantKey(string deviceId, Guid offerId) => deviceId + ":" + offerId.ToString("N");

    private bool IsGranted(string deviceId, Guid offerId) =>
        _fetchGrants.ContainsKey(GrantKey(deviceId, offerId));

    // ===================== CLIENT =====================

    private void PingKnown()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        foreach (var ip in _activeIps.Keys.ToList())
            if (IPAddress.TryParse(ip, out var addr))
                _ = Task.Run(() => PingAsync(addr, ct), ct);
        ExpirePeers();
    }

    private async Task ScanAsync(CancellationToken ct, string reason)
    {
        List<IPAddress> hosts;
        try { hosts = NetworkScan.LocalSubnetHosts(2048); }
        catch { return; }
        if (hosts.Count == 0) { Log.Write("[Transport] scansione: subnet assente/troppo grande"); return; }
        Log.Write($"[Transport] scansione ({reason}): {hosts.Count} host");
        try
        {
            await Parallel.ForEachAsync(hosts,
                new ParallelOptions { MaxDegreeOfParallelism = 100, CancellationToken = ct },
                async (ip, token) => await PingAsync(ip, token, 700));
        }
        catch (OperationCanceledException) { }
    }

    private async Task PingAsync(IPAddress addr, CancellationToken ct, int timeoutMs = 3000)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(addr, _config.Port, cts.Token);
            await using var stream = client.GetStream();

            var s = await ClientHandshakeAsync(stream, cts.Token);
            if (s == null || s.R.PeerDeviceId == _identity.DeviceId) return;
            var trusted = _trust.Matches(s.R.PeerDeviceId, s.R.PeerPublicKeyDer);

            await WriteSessionAsync(stream, s.Cipher, BuildPing(), cts.Token);
            var reply = await ReadSessionAsync(stream, s.Cipher, cts.Token);
            if (reply == null || reply.Length == 0 || reply[0] != OpPing) return;

            var (name, port, gossip, work) = ParsePing(reply[1..]);
            RememberIp(addr.ToString());
            UpsertPeer(s.R.PeerDeviceId, name, addr, port, s.R.PeerPublicKeyDer, trusted, work);
            if (trusted) ProcessGossip(gossip);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Transport] ping {addr}: {ex.Message}");
        }
    }

    /// <summary>Invia un contenuto (push) a tutti i dispositivi FIDATI.</summary>
    public async Task SendAsync(ClipboardPayload payload)
    {
        var targets = _peers.Values.Where(p => p.Trusted).ToList();
        if (targets.Count == 0) return;
        var frame = Concat(new[] { OpPush }, payload.Serialize());
        await Task.WhenAll(targets.Select(p => PushToPeerAsync(p, frame)));
    }

    /// <summary>
    /// Invio mirato a UN destinatario, anche non accoppiato: l'altro lato vede una
    /// richiesta e decide. Se il contenuto sono file, ad accettazione avvenuta gli
    /// si concede il permesso di prelevarli.
    /// </summary>
    public async Task<SendOutcome> SendToAsync(Peer peer, ClipboardPayload payload)
    {
        // Verso un dispositivo già fidato non ha senso far comparire una richiesta:
        // è la stessa mesh, si usa il percorso normale.
        var op = peer.Trusted ? OpPush : OpOffer;
        if (payload.Kind == PayloadKind.Files && payload.Offer != null && !peer.Trusted)
            _fetchGrants.TryAdd(GrantKey(peer.DeviceId, payload.Offer.OfferId), 1);

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); // l'utente deve rispondere
            await client.ConnectAsync(peer.Address, peer.Port, cts.Token);
            await using var stream = client.GetStream();

            var s = await ClientHandshakeAsync(stream, cts.Token);
            if (s == null || s.R.PeerDeviceId != peer.DeviceId) return SendOutcome.Failed;

            await WriteSessionAsync(stream, s.Cipher, Concat(new[] { op }, payload.Serialize()), cts.Token);
            if (op == OpPush) return SendOutcome.Delivered;

            var reply = await ReadSessionAsync(stream, s.Cipher, cts.Token);
            var accepted = reply is { Length: > 0 } && reply[0] == 1;
            if (!accepted && payload.Offer != null)
                _fetchGrants.TryRemove(GrantKey(peer.DeviceId, payload.Offer.OfferId), out _);
            return accepted ? SendOutcome.Delivered : SendOutcome.Declined;
        }
        catch (Exception ex)
        {
            Log.Write($"[Transport] invio a {peer.Label} fallito: {ex.Message}");
            if (payload.Offer != null)
                _fetchGrants.TryRemove(GrantKey(peer.DeviceId, payload.Offer.OfferId), out _);
            return SendOutcome.Failed;
        }
    }

    private async Task PushToPeerAsync(Peer peer, byte[] opFrame)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(peer.Address, peer.Port, cts.Token);
            await using var stream = client.GetStream();
            var s = await ClientHandshakeAsync(stream, cts.Token);
            if (s == null || !_trust.Matches(s.R.PeerDeviceId, s.R.PeerPublicKeyDer)) return;
            await WriteSessionAsync(stream, s.Cipher, opFrame, cts.Token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Transport] push {peer}: {ex.Message}");
        }
    }

    // ===================== PAIRING =====================

    public async Task<(PairOutcome Outcome, string Name)> PairAsync(IPAddress ip, int port, string nameHint, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            await client.ConnectAsync(ip, port, cts.Token);
            client.ReceiveTimeout = 120_000;
            await using var stream = client.GetStream();

            var s = await ClientHandshakeAsync(stream, cts.Token);
            if (s == null) return (PairOutcome.Failed, nameHint);
            if (s.R.PeerDeviceId == _identity.DeviceId) return (PairOutcome.Failed, nameHint);

            await WriteSessionAsync(stream, s.Cipher, BuildPair(), cts.Token);

            var mine = PairingConfirm?.Invoke(
                new PairingPrompt(s.R.Sas, nameHint, DeviceIdentity.ShortFingerprint(s.R.PeerDeviceId))) ?? false;
            await WriteSessionAsync(stream, s.Cipher, new byte[] { (byte)(mine ? 1 : 0) }, cts.Token);

            var peerAck = await ReadSessionAsync(stream, s.Cipher, cts.Token);
            var theirs = peerAck is { Length: > 0 } && peerAck[0] == 1;

            if (mine && theirs)
            {
                _trust.Trust(s.R.PeerDeviceId, nameHint, s.R.PeerPublicKeyDer);
                RememberIp(ip.ToString());
                UpsertPeer(s.R.PeerDeviceId, nameHint, ip, port, s.R.PeerPublicKeyDer, trusted: true);
                Log.Write($"[Pairing] fidato ora: {nameHint} ({DeviceIdentity.ShortFingerprint(s.R.PeerDeviceId)})");
                return (PairOutcome.Paired, nameHint);
            }
            return (PairOutcome.Rejected, nameHint);
        }
        catch (Exception ex)
        {
            Log.Write($"[Pairing] fallito verso {ip}: {ex.Message}");
            return (PairOutcome.Failed, nameHint);
        }
    }

    private async Task HandlePairServerAsync(NetworkStream stream, Session s, IPAddress remote, byte[] body, CancellationToken ct)
    {
        var (clientName, clientPort, _, _) = ParsePing(body); // stesso formato (name, port, gossip, identità)

        var mine = PairingConfirm?.Invoke(
            new PairingPrompt(s.R.Sas, clientName, DeviceIdentity.ShortFingerprint(s.R.PeerDeviceId))) ?? false;
        await WriteSessionAsync(stream, s.Cipher, new byte[] { (byte)(mine ? 1 : 0) }, ct);

        var peerAck = await ReadSessionAsync(stream, s.Cipher, ct);
        var theirs = peerAck is { Length: > 0 } && peerAck[0] == 1;

        if (mine && theirs)
        {
            _trust.Trust(s.R.PeerDeviceId, clientName, s.R.PeerPublicKeyDer);
            RememberIp(remote.ToString());
            UpsertPeer(s.R.PeerDeviceId, clientName, remote, clientPort, s.R.PeerPublicKeyDer, trusted: true);
            Log.Write($"[Pairing] fidato ora: {clientName} ({DeviceIdentity.ShortFingerprint(s.R.PeerDeviceId)})");
        }
    }

    // ===================== FETCH (file on-demand) =====================

    /// <summary>Codice d'errore di protocollo: l'offerta non esiste più sull'host.</summary>
    private const string WireErrorOfferGone = "offer-gone";

    /// <summary>
    /// Converte il codice d'errore ricevuto dall'altro PC in un testo nella lingua
    /// di CHI LEGGE. I peer più vecchi mandano già la frase in chiaro: in quel caso
    /// la si mostra così com'è.
    /// </summary>
    private static string TranslateWireError(string wire) =>
        wire == WireErrorOfferGone ? L.T("error.offerGone") : wire;

    public async Task<List<string>> FetchAsync(Peer owner, Guid offerId, string destDir, CancellationToken ct,
        IProgress<FetchProgress>? progress = null)
    {
        Directory.CreateDirectory(destDir);
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(owner.Address, owner.Port, cts.Token);
        client.ReceiveTimeout = 120_000;
        await using var stream = client.GetStream();

        var s = await ClientHandshakeAsync(stream, ct);
        // O il proprietario è fidato, oppure è un collega di cui abbiamo accettato
        // proprio questa offerta. In entrambi i casi dev'essere il dispositivo atteso.
        if (s == null || s.R.PeerDeviceId != owner.DeviceId ||
            (!_trust.Matches(s.R.PeerDeviceId, s.R.PeerPublicKeyDer) &&
             !_acceptedOffers.ContainsKey(GrantKey(s.R.PeerDeviceId, offerId))))
            throw new IOException(L.T("error.notTrusted"));

        await WriteSessionAsync(stream, s.Cipher, Concat(new[] { OpFetch }, offerId.ToByteArray()), ct);

        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileStream? current = null;
        var doneBytes = 0L;
        var doneFiles = 0;
        var currentName = "";
        var lastReport = 0L;
        try
        {
            while (true)
            {
                var plain = await ReadSessionAsync(stream, s.Cipher, ct);
                if (plain == null || plain.Length == 0) break;
                var t = plain[0];
                if (t == FEnd) break;
                if (t == FError) throw new IOException(TranslateWireError(Encoding.UTF8.GetString(plain, 5, ReadInt(plain, 1))));
                if (t == FHeader)
                {
                    current?.Dispose(); current = null;
                    var isDir = plain[1] != 0;
                    var relLen = ReadInt(plain, 10);
                    var rel = Encoding.UTF8.GetString(plain, 14, relLen);

                    // Il nome lo decide il mittente: se punta fuori dalla cartella di
                    // destinazione l'entry si scarta e si prosegue. I frame FData che
                    // seguono trovano current == null e vengono ignorati da soli.
                    var target = SafeTarget(destDir, rel);
                    if (target == null)
                    {
                        Log.Write($"[Fetch] entry scartata, percorso non sicuro: {rel}");
                        continue;
                    }

                    if (!rel.Contains('/')) topNames.Add(rel);
                    if (isDir) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        current = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                        currentName = rel;
                        progress?.Report(new FetchProgress(currentName, doneBytes, doneFiles));
                    }
                }
                else if (t == FData && current != null)
                {
                    await current.WriteAsync(plain.AsMemory(1), ct);
                    doneBytes += plain.Length - 1;
                    // niente report a raffica: al massimo ~10 al secondo
                    var now = Environment.TickCount64;
                    if (progress != null && now - lastReport >= 100)
                    {
                        lastReport = now;
                        progress.Report(new FetchProgress(currentName, doneBytes, doneFiles));
                    }
                }
                else if (t == FEntryEnd)
                {
                    if (current != null) doneFiles++;
                    current?.Dispose(); current = null;
                }
            }
        }
        finally { current?.Dispose(); }
        progress?.Report(new FetchProgress(currentName, doneBytes, doneFiles));

        return topNames.Select(n => Path.Combine(destDir, n))
            .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    private async Task ServeFetchAsync(NetworkStream stream, Session s, Guid offerId, CancellationToken ct)
    {
        var offer = _offerStore.Get(offerId);
        if (offer == null || offer.RootParents == null)
        {
            // sul filo va un CODICE, non una frase: il testo lo traduce chi riceve,
            // nella sua lingua (vedi TranslateWireError)
            await WriteSessionAsync(stream, s.Cipher, ErrorFrame(WireErrorOfferGone), ct);
            return;
        }
        foreach (var e in offer.Entries)
        {
            if (ct.IsCancellationRequested) return;
            var abs = offer.ResolveLocal(e);
            if (e.IsDir) { await WriteSessionAsync(stream, s.Cipher, HeaderFrame(e, 0), ct); await WriteSessionAsync(stream, s.Cipher, new[] { FEntryEnd }, ct); continue; }
            if (abs == null || !File.Exists(abs)) continue;
            long size; try { size = new FileInfo(abs).Length; } catch { continue; }
            await WriteSessionAsync(stream, s.Cipher, HeaderFrame(e, size), ct);
            try
            {
                await using var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[ChunkSize];
                int n;
                while ((n = await fs.ReadAsync(buf.AsMemory(0, ChunkSize), ct)) > 0)
                    await WriteSessionAsync(stream, s.Cipher, Concat(new[] { FData }, buf[..n]), ct);
            }
            catch { }
            await WriteSessionAsync(stream, s.Cipher, new[] { FEntryEnd }, ct);
        }
        await WriteSessionAsync(stream, s.Cipher, new[] { FEnd }, ct);
    }

    /// <summary>
    /// Percorso di destinazione per un'entry ricevuta, oppure null se non è sicuro.
    ///
    /// Il percorso relativo arriva dal peer, quindi è dato ostile. Senza controlli
    /// un mittente accoppiato scriverebbe ovunque: Path.Combine, se il secondo
    /// argomento è assoluto, SCARTA il primo e restituisce l'assoluto, perciò
    /// bastava "C:/.../Esecuzione automatica/x.exe" per ottenere esecuzione di
    /// codice al login. Si rifiutano percorsi radicati, risalite e due punti
    /// (unità e stream alternativi NTFS), e si verifica che il risultato
    /// normalizzato resti dentro la cartella di destinazione.
    /// </summary>
    private static string? SafeTarget(string destDir, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Contains('\0')) return null;

        if (Path.IsPathRooted(rel)) return null;

        // Si separa su entrambi i caratteri per non dover scrivere il backslash
        // a mano: su Windows Alt e' '/', Directory e' il rovescio.
        var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                 StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        foreach (var seg in segments)
        {
            if (seg == ".." || seg == "." || seg.Contains(':')) return null;
            if (seg.EndsWith(' ') || seg.EndsWith('.')) return null; // insidie di Win32
        }

        string root, full;
        try
        {
            root = Path.GetFullPath(destDir);
            full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        }
        catch { return null; }

        // Il confronto vuole il separatore finale, altrimenti "C:\dest" accetterebbe "C:\destinazione".
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static byte[] HeaderFrame(FileEntry e, long size)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(FHeader); w.Write(e.IsDir); w.Write(size);
        var rel = Encoding.UTF8.GetBytes(e.RelativePath); w.Write(rel.Length); w.Write(rel);
        return ms.ToArray();
    }

    private static byte[] ErrorFrame(string msg)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(FError); var b = Encoding.UTF8.GetBytes(msg); w.Write(b.Length); w.Write(b);
        return ms.ToArray();
    }

    // ===================== PEER REGISTRY / GOSSIP =====================

    private void UpsertPeer(string deviceId, string name, IPAddress addr, int port, byte[]? pub, bool trusted,
                           WorkIdentity? work = null)
    {
        var isNew = !_peers.ContainsKey(deviceId);
        _peers.AddOrUpdate(deviceId,
            _ => new Peer { DeviceId = deviceId, Name = name, Address = addr, Port = port, PublicKeyDer = pub, Trusted = trusted, Work = work },
            (_, p) => { p.Name = name; p.Address = addr; p.Port = port; p.PublicKeyDer = pub; p.Trusted = trusted; p.LastSeenUtc = DateTime.UtcNow; if (work != null) p.Work = work; return p; });
        if (isNew) Log.Write($"[Transport] peer: {name} @ {addr} {(trusted ? "[FIDATO]" : "[non fidato]")}");
        PeersChanged?.Invoke();
    }

    private void ExpirePeers()
    {
        var now = DateTime.UtcNow; var changed = false;
        foreach (var kv in _peers)
            if (now - kv.Value.LastSeenUtc > PeerTtl && _peers.TryRemove(kv.Key, out _)) changed = true;
        if (changed) PeersChanged?.Invoke();
    }

    private sealed record GossipEntry(string DeviceId, string Name, string Ip, int Port, byte[] Pub);

    /// <summary>Su sessione fidata, adotta (introducer) i peer raccontati dal peer.</summary>
    private void ProcessGossip(List<GossipEntry> gossip)
    {
        foreach (var g in gossip)
        {
            if (g.DeviceId == _identity.DeviceId) continue;
            if (!_trust.IsTrusted(g.DeviceId) && g.Pub.Length > 0)
            {
                _trust.Trust(g.DeviceId, g.Name, g.Pub);
                Log.Write($"[Mesh] introdotto e fidato: {g.Name} ({DeviceIdentity.ShortFingerprint(g.DeviceId)})");
            }
            if (IPAddress.TryParse(g.Ip, out _)) RememberIp(g.Ip);
        }
    }

    private byte[] BuildPing()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(OpPing);
        WStr(w, _config.DisplayName);
        w.Write(_config.Port);
        var trusted = _peers.Values.Where(p => p.Trusted && p.PublicKeyDer != null).Take(64).ToList();
        w.Write(trusted.Count);
        foreach (var p in trusted)
        {
            WStr(w, p.DeviceId); WStr(w, p.Name); WStr(w, p.Address.ToString()); w.Write(p.Port); WBuf(w, p.PublicKeyDer!);
        }

        // Identità aziendale IN CODA: le versioni precedenti leggono fin qui e
        // ignorano il resto, quindi il campo si aggiunge senza rompere nulla.
        var me = SelfWork;
        WStr(w, me?.TenantId ?? ""); WStr(w, me?.ObjectId ?? "");
        WStr(w, me?.UserPrincipalName ?? ""); WStr(w, me?.DisplayName ?? "");
        return ms.ToArray();
    }

    private byte[] BuildPair()
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(OpPair);
        WStr(w, _config.DisplayName);
        w.Write(_config.Port);
        w.Write(0); // nessun gossip nel pair
        return ms.ToArray();
    }

    private static (string Name, int Port, List<GossipEntry> Gossip, WorkIdentity? Work) ParsePing(byte[] body)
    {
        var gossip = new List<GossipEntry>();
        try
        {
            using var ms = new MemoryStream(body); using var r = new BinaryReader(ms);
            var name = RStr(r);
            var port = r.ReadInt32();
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var id = RStr(r); var nm = RStr(r); var ip = RStr(r); var pt = r.ReadInt32(); var pub = RBuf(r);
                gossip.Add(new GossipEntry(id, nm, ip, pt, pub));
            }

            // Coda opzionale: i peer di versione precedente non la mandano, e la
            // sua assenza non deve far perdere nome e porta letti sopra.
            WorkIdentity? work = null;
            try
            {
                var tid = RStr(r); var oid = RStr(r); var upn = RStr(r); var dn = RStr(r);
                if (tid.Length > 0 && oid.Length > 0) work = new WorkIdentity(tid, oid, upn, dn);
            }
            catch (EndOfStreamException) { }

            return (name, port, gossip, work);
        }
        catch { return ("?", 0, gossip, null); }
    }

    private void RememberIp(string ip)
    {
        if (!_activeIps.TryAdd(ip, 1)) return;
        lock (_cacheGate)
        {
            if (!_config.KnownPeerIps.Contains(ip))
            {
                _config.KnownPeerIps.Add(ip);
                if (_config.KnownPeerIps.Count > 64) _config.KnownPeerIps.RemoveAt(0);
                _config.Save();
            }
        }
    }

    private string PeerName(string deviceId) =>
        _peers.TryGetValue(deviceId, out var p) ? p.Name : DeviceIdentity.ShortFingerprint(deviceId);

    // ===================== HANDSHAKE =====================

    private sealed record Session(SessionCipher Cipher, HandshakeResult R);

    private async Task<Session?> ClientHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var hs = new Handshaker(_identity);
        try
        {
            using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            { w.Write(Magic); w.Write(Version); WBuf(w, hs.IdPublicKey); WBuf(w, hs.EphPublicKey); }
            await WritePlainAsync(stream, ms.ToArray(), ct);

            var h2 = await ReadPlainAsync(stream, ct);
            if (h2 == null) return null;
            using var r = new BinaryReader(new MemoryStream(h2));
            var idS = RBuf(r); var ephS = RBuf(r); var sigS = RBuf(r);

            var res = hs.Complete(idS, ephS, selfIsInitiator: true);
            if (!Handshaker.VerifyPeer(idS, res.Transcript, sigS)) { Log.Write("[Handshake] firma server non valida"); return null; }

            var sigC = hs.SignTranscript(res.Transcript);
            using var ms3 = new MemoryStream(); using (var w = new BinaryWriter(ms3, Encoding.UTF8, true)) WBuf(w, sigC);
            await WritePlainAsync(stream, ms3.ToArray(), ct);
            return new Session(new SessionCipher(res.SessionKey), res);
        }
        finally { hs.Dispose(); }
    }

    private async Task<Session?> ServerHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var hs = new Handshaker(_identity);
        try
        {
            var h1 = await ReadPlainAsync(stream, ct);
            if (h1 == null) return null;
            using var r = new BinaryReader(new MemoryStream(h1));
            var magic = r.ReadBytes(2);
            if (magic.Length != 2 || magic[0] != Magic[0] || magic[1] != Magic[1]) return null;
            if (r.ReadByte() != Version) return null;
            var idC = RBuf(r); var ephC = RBuf(r);

            var res = hs.Complete(idC, ephC, selfIsInitiator: false);
            var sigS = hs.SignTranscript(res.Transcript);
            using var ms = new MemoryStream(); using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            { WBuf(w, hs.IdPublicKey); WBuf(w, hs.EphPublicKey); WBuf(w, sigS); }
            await WritePlainAsync(stream, ms.ToArray(), ct);

            var h3 = await ReadPlainAsync(stream, ct);
            if (h3 == null) return null;
            using var r3 = new BinaryReader(new MemoryStream(h3));
            var sigC = RBuf(r3);
            if (!Handshaker.VerifyPeer(idC, res.Transcript, sigC)) { Log.Write("[Handshake] firma client non valida"); return null; }
            return new Session(new SessionCipher(res.SessionKey), res);
        }
        finally { hs.Dispose(); }
    }

    // ===================== FRAMING =====================

    private async Task WriteSessionAsync(NetworkStream stream, SessionCipher c, byte[] plain, CancellationToken ct) =>
        await WritePlainAsync(stream, c.Seal(plain), ct);

    private async Task<byte[]?> ReadSessionAsync(NetworkStream stream, SessionCipher c, CancellationToken ct)
    {
        var blob = await ReadPlainAsync(stream, ct);
        return blob == null ? null : c.Open(blob);
    }

    private async Task WritePlainAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)(payload.Length >> 24); frame[1] = (byte)(payload.Length >> 16);
        frame[2] = (byte)(payload.Length >> 8); frame[3] = (byte)payload.Length;
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<byte[]?> ReadPlainAsync(NetworkStream stream, CancellationToken ct)
    {
        var len = await ReadExactAsync(stream, 4, ct);
        if (len == null) return null;
        var n = (len[0] << 24) | (len[1] << 16) | (len[2] << 8) | len[3];
        var max = _config.MaxTransferMb * 1024L * 1024L + (4 << 20);
        if (n <= 0 || n > max) return null;
        return await ReadExactAsync(stream, n, ct);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count]; var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    private static int ReadInt(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }
    private static void WBuf(BinaryWriter w, byte[] b) { w.Write(b.Length); w.Write(b); }
    /// <summary>
    /// Campi di framing (nomi, ID, chiavi, firme): nessuno arriva a un KB. Il tetto
    /// evita che un prefisso di lunghezza malevolo faccia allocare centinaia di MB,
    /// visto che ReadBytes non lancia a fine stream ma si limita a leggere meno.
    /// </summary>
    private const int MaxFieldBytes = 64 * 1024;

    private static byte[] RBuf(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n < 0 || n > MaxFieldBytes) throw new InvalidDataException($"campo di {n} byte fuori limite");
        return r.ReadBytes(n);
    }
    private static void WStr(BinaryWriter w, string s) => WBuf(w, Encoding.UTF8.GetBytes(s));
    private static string RStr(BinaryReader r) => Encoding.UTF8.GetString(RBuf(r));

    public void Dispose() => Stop();
}
