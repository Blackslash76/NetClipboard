using System.Net;
using System.Net.Sockets;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;

namespace NetClipboard.E2E;

/// <summary>
/// Un'istanza completa del trasporto: identita' effimera, archivio della fiducia,
/// offerte e permessi, tutto in una cartella temporanea sua. Nove di questi
/// convivono nello stesso processo e si parlano su loopback.
///
/// Niente tocca <c>%AppData%\NetClipboard</c>: l'identita' e' effimera (non scrive
/// identity.key), lo stato sta in <see cref="AppConfig.StateDir"/> e il log e'
/// dirottato altrove. Una prova non deve andare a rovistare fra i dati di chi
/// lavora, ne' bussare all'applicazione in esecuzione.
/// </summary>
public sealed class Node : IDisposable
{
    public string Name { get; }
    public string Dir { get; }
    public AppConfig Config { get; }
    public DeviceIdentity Identity { get; }
    public TrustStore Trust { get; }
    public OfferStore Offers { get; }
    public ClipboardTransport Transport { get; }

    /// <summary>Tutto cio' che e' arrivato, in ordine. Ci si scrive da thread di rete.</summary>
    public readonly List<ReceivedClip> Received = new();
    private readonly Lock _gate = new();

    public Node(string name, string rootDir)
    {
        Name = name;
        Dir = Path.Combine(rootDir, name);
        Directory.CreateDirectory(Dir);

        Config = new AppConfig
        {
            DisplayName = name,
            StateDir = Dir,
            Port = FreePort(),
            // Senza questo il primo avvio si mette a spazzolare l'intera subnet:
            // lento, e per giunta un rumore che una prova non deve fare in rete.
            AutoScanDiscovery = false,
        };

        Identity = DeviceIdentity.CreateEphemeral();
        Trust = new TrustStore(Path.Combine(Dir, "trusted.json"));
        Offers = new OfferStore(Config);
        Transport = new ClipboardTransport(Config, Identity, Trust, Offers);
        Transport.Received += clip => { lock (_gate) Received.Add(clip); };
        Transport.Start();
    }

    public int Port => Config.Port;
    public string DeviceId => Identity.DeviceId;

    /// <summary>Chiede al sistema una porta libera e la restituisce subito: fra il rilascio
    /// e il nostro listener la finestra e' minima, e vale piu' di un numero fisso che
    /// potrebbe essere gia' occupato sulla macchina di chi esegue la prova.</summary>
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public IReadOnlyList<ReceivedClip> Snapshot()
    {
        lock (_gate) return Received.ToList();
    }

    public void ClearReceived()
    {
        lock (_gate) Received.Clear();
    }

    /// <summary>Il peer come lo vede questo nodo, se lo ha gia' incontrato.</summary>
    public Peer? PeerOf(Node other) =>
        Transport.Peers.FirstOrDefault(p => p.DeviceId == other.DeviceId);

    /// <summary>Contatta l'altro nodo: rinfresca la presenza e fa girare il gossip.</summary>
    public Task PingAsync(Node other) => Transport.PingNowAsync(IPAddress.Loopback, other.Port);

    /// <summary>Accoppiamento vero, con handshake e SAS, verso l'altro nodo.</summary>
    public Task<(PairOutcome Outcome, string Name)> PairAsync(Node other) =>
        Transport.PairAsync(IPAddress.Loopback, other.Port, other.Name, CancellationToken.None);

    public void Dispose()
    {
        try { Transport.Dispose(); } catch { }
    }
}
