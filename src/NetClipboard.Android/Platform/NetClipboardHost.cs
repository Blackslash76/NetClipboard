using System.Net;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;

namespace NetClipboard.Droid.Platform;

/// <summary>
/// Il nucleo dell'applicazione messo in piedi: configurazione, identita',
/// fiducia, trasporto, scoperta. E' l'equivalente di cio' che su Windows fa
/// TrayContext, meno l'interfaccia.
///
/// Vive nel servizio in primo piano e non nell'attivita': su Android l'attivita'
/// viene distrutta ogni volta che si gira il telefono, e un trasporto che si
/// riavvia a ogni rotazione perderebbe le connessioni e rifarebbe l'handshake
/// con tutti. Il servizio invece resta.
/// </summary>
public sealed class NetClipboardHost : IDisposable
{
    private static NetClipboardHost? _current;

    /// <summary>L'istanza in vita, se il servizio e' avviato. L'interfaccia legge di qui.</summary>
    public static NetClipboardHost? Current => _current;

    public AppConfig Config { get; }
    public DeviceIdentity Identity { get; }
    public TrustStore Trust { get; }
    public OfferStore Offers { get; }
    public ClipboardTransport Transport { get; }

    private readonly PeerDiscovery _discovery;

    /// <summary>Contenuto arrivato da un dispositivo fidato o da un invio accettato.</summary>
    public event Action<ReceivedClip>? Received;

    /// <summary>L'elenco dei dispositivi in rete e' cambiato.</summary>
    public event Action? PeersChanged;

    public NetClipboardHost(string appDataDir, string deviceName)
    {
        AppConfig.UseAppDataDir(appDataDir);
        AppConfig.DeviceName = () => deviceName;

        Config = AppConfig.Load();
        Config.Save();

        Identity = DeviceIdentity.LoadOrCreate(new AndroidSecretProtector());
        Trust = new TrustStore();
        Offers = new OfferStore(Config);

        Log.Start($"NetClipboard Android · {Config.DisplayName} · " +
                  $"device {DeviceIdentity.ShortFingerprint(Identity.DeviceId)} · porta {Config.Port} · " +
                  $"fidati: {Trust.All.Count}");

        // Nessun analizzatore: Android non ne espone uno a un'applicazione
        // qualunque, e dichiararlo assente e' l'unica risposta onesta. Il core
        // lo sa fare — non mostrera' mai un bollino di verifica.
        Transport = new ClipboardTransport(Config, Identity, Trust, Offers)
        {
            PairingConfirm = AskPairing,
            OfferConfirm = AskOffer,
            IntroductionConfirm = AskIntroduction,
        };
        Transport.Received += clip =>
        {
            // Negli appunti ci mette il SERVIZIO, non la schermata: il contenuto
            // arriva anche ad applicazione chiusa, ed e' proprio quello il caso
            // normale — si copia sul PC e si va a incollare sul telefono. Finche'
            // lo faceva la schermata, tutto cio' che arrivava con l'app chiusa
            // andava perso senza che niente lo segnalasse.
            AndroidClipboard.Write(clip.Payload);
            Received?.Invoke(clip);
        };
        Transport.PeersChanged += () => PeersChanged?.Invoke();

        _discovery = new PeerDiscovery(Config, ip => Transport.AddCandidate(ip));
        _current = this;
    }

    public void Start()
    {
        Transport.Start();
        _discovery.Start();
    }

    public void Stop()
    {
        _discovery.Stop();
        Transport.Stop();
    }

    public IReadOnlyCollection<Peer> Peers => Transport.Peers;

    /// <summary>
    /// Accoppia, <b>fuori dal thread dell'interfaccia</b>.
    ///
    /// Il <c>Task.Run</c> non è pignoleria: durante il pairing il trasporto chiama
    /// <c>PairingConfirm</c> e ASPETTA la risposta. Se la catena girasse sul thread
    /// dell'interfaccia — e girerebbe, perché la si avvia da un pulsante e ogni
    /// await tornerebbe lì — quel blocco impedirebbe alla domanda di comparire, e
    /// la domanda è ciò che si sta aspettando. Si pianta tutto, in silenzio, e il
    /// pulsante sembra semplicemente non funzionare.
    /// </summary>
    public Task<(PairOutcome Outcome, string Name)> PairAsync(Peer peer, CancellationToken ct = default) =>
        Task.Run(() => Transport.PairAsync(peer.Address, peer.Port, peer.Name, ct), ct);

    /// <summary>
    /// Manda un contenuto a tutti i propri dispositivi fidati. Anche questo fuori
    /// dal thread dell'interfaccia: qui non c'è niente che aspetti una risposta,
    /// ma sono connessioni a tutti i pari, e la schermata non deve fermarsi.
    /// </summary>
    public Task SendAsync(ClipboardPayload payload) => Task.Run(() => Transport.SendAsync(payload));

    public void AddManualPeer(IPAddress ip) => Transport.AddCandidate(ip);

    // ----- le tre conferme, tutte con la stessa regola: nessuna risposta = no -----

    /// <summary>
    /// Il token scatta se l'altro dispositivo ha annullato: la domanda sparisce
    /// dallo schermo da sola, invece di restare a chiedere un confronto che
    /// dall'altra parte non serve piu'.
    /// </summary>
    private bool AskPairing(PairingPrompt p, CancellationToken peerGaveUp) =>
        Prompts.Ask(new PromptRequest(
            L.T("sas.heading"),
            L.T("sas.peerLine", p.PeerName, p.Fingerprint) + "\n\n" + p.Sas + "\n\n" + L.T("sas.warning"),
            L.T("sas.confirm"),
            L.T("common.cancel")), TimeSpan.FromSeconds(110), peerGaveUp) == true;

    private bool AskOffer(IncomingOffer o) =>
        Prompts.Ask(new PromptRequest(
            L.T("incoming.heading"),
            L.T("incoming.fromLine", o.FromLabel, o.Preview) + "\n\n" + L.T("incoming.warning"),
            L.T("incoming.accept"),
            L.T("common.cancel")), TimeSpan.FromSeconds(50)) == true;

    /// <summary>
    /// Qui il null conta: il core lo interpreta come "non ho risposto" e
    /// riproporra' la presentazione fra dieci minuti, invece di darla per
    /// rifiutata per sempre.
    /// </summary>
    private bool? AskIntroduction(IntroductionPrompt i) =>
        Prompts.Ask(new PromptRequest(
            L.T("intro.heading"),
            L.T("intro.whoLine", i.IntroducerName, i.NewDeviceName) + "\n" +
            L.T("intro.fingerprint", i.Fingerprint) + "\n\n" + L.T("intro.warning"),
            L.T("intro.accept"),
            L.T("intro.refuse")), TimeSpan.FromSeconds(50));

    public void Dispose()
    {
        Stop();
        Transport.Dispose();
        _discovery.Dispose();
        Identity.Dispose();
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
