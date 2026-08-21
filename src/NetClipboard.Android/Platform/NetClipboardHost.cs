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

    /// <summary>
    /// La stessa cronologia cifrata dell'applicazione Windows: e' nel core, e qui
    /// non e' costata niente. E' cio' che permette di NON sovrascrivere gli
    /// appunti a ogni arrivo — il contenuto si mette da parte, e chi usa il
    /// telefono decide quando prenderlo.
    /// </summary>
    public ClipboardHistory History { get; }

    /// <summary>Quanto puo' occupare in tutto la cronologia sul telefono, allegati compresi.</summary>
    private const long MaxHistoryBytes = 64L * 1024 * 1024;

    private readonly PeerDiscovery _discovery;

    /// <summary>
    /// Contenuto arrivato da un dispositivo fidato o da un invio accettato, gia'
    /// messo in cronologia. La voce serve a chi deve proporlo: la notifica porta
    /// il suo identificativo, e al tocco lo si ritrova.
    /// </summary>
    public event Action<ReceivedClip, HistoryItem>? Received;

    /// <summary>L'elenco dei dispositivi in rete e' cambiato.</summary>
    public event Action? PeersChanged;

    public NetClipboardHost(string appDataDir, string deviceName)
    {
        AppConfig.UseAppDataDir(appDataDir);
        AppConfig.DeviceName = () => deviceName;

        Config = AppConfig.Load();
        Config.Save();

        var protector = new AndroidSecretProtector();
        Identity = DeviceIdentity.LoadOrCreate(protector);
        Trust = new TrustStore();
        Offers = new OfferStore(Config);
        // Tetto stretto, e molto piu' basso di quello del PC: qui lo spazio non e'
        // nostro. Con le sole 30 voci di HistorySize, trenta immagini al massimo
        // che il trasporto accetta farebbero un gigabyte e mezzo sul telefono.
        History = new ClipboardHistory(Config, protector, MaxHistoryBytes);

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
            // NON si sovrascrivono gli appunti.
            //
            // Su Windows si fa, ed e' ragionevole: la clipboard la si ricicla di
            // continuo e non succede niente di visibile. Su Android no, per tre
            // motivi che sul PC non valgono: da Android 13 il sistema mostra un
            // avviso a OGNI scrittura degli appunti (quindi ogni copia fatta sul
            // PC farebbe comparire un popup sul telefono); su un telefono si sta
            // quasi sempre scrivendo qualcosa, e perdere cio' che si era copiato
            // da fastidio; e il telefono spesso non ce l'hai davanti, quindi la
            // sovrascrittura e' invisibile finche' non morde.
            //
            // Il contenuto si mette in cronologia — non si perde niente, che e' la
            // cosa importante — e chi avvisa propone un'azione. Decide l'utente.
            var item = History.Add(clip.Payload, clip.FromName, isLocal: false, fromExternal: clip.FromExternal);
            Received?.Invoke(clip, item);

            // Gli arrivi sono i momenti in cui lo spazio cresce: e' li' che ha
            // senso guardare se c'e' da buttare. La guardia di tempo dentro
            // Housekeeping fa in modo che non diventi un lavoro a ogni copia.
            Housekeeping.Run(Config);
        };
        Transport.PeersChanged += () => PeersChanged?.Invoke();

        _discovery = new PeerDiscovery(Config, ip => Transport.AddCandidate(ip));
        _current = this;
    }

    public void Start()
    {
        Transport.Start();
        _discovery.Start();

        Housekeeping.Run(Config, force: true);
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

    /// <summary>
    /// Manda a tutti i fidati cio' che l'utente ha condiviso, e restituisce a
    /// quanti e' partito.
    ///
    /// Se sono file, l'offerta va REGISTRATA prima: sul filo viaggia solo
    /// l'elenco, e i byte verranno chiesti dopo. Senza registrazione quel "dopo"
    /// non troverebbe niente, e chi riceve vedrebbe un'offerta che non si scarica.
    /// </summary>
    public async Task<int> ShareAsync(ClipboardPayload payload)
    {
        if (payload.Kind == PayloadKind.Files && payload.Offer != null)
            Offers.Register(payload.Offer);

        var trusted = Transport.TrustedPeers.Count;
        if (trusted == 0) return 0;

        await SendAsync(payload);
        return trusted;
    }

    public void AddManualPeer(IPAddress ip) => Transport.AddCandidate(ip);

    /// <summary>
    /// Mette negli appunti una voce della cronologia. E' il gesto deliberato che
    /// sostituisce la sovrascrittura automatica: lo scatena chi tocca la notifica
    /// o sceglie dall'elenco, mai la rete da sola.
    /// </summary>
    /// <returns>
    /// Falso se il contenuto negli appunti non ci puo' andare. Restano fuori solo
    /// i FILE: quelli non sono un contenuto ma un'offerta da prelevare, e la
    /// strada e' <see cref="MaterializeAsync"/>. Chi chiama deve dirlo, invece di
    /// far credere che sia andata.
    /// </returns>
    public bool PutInClipboard(string historyItemId)
    {
        var item = History.GetById(historyItemId);
        if (item == null) return false;

        // Un'immagine negli appunti di Android non e' un blocco di byte: e' un
        // riferimento a un file. Si scrive dove il provider puo' prestarlo e si
        // mette in clipboard quel riferimento — e' l'unico modo perche'
        // "incolla" funzioni in un'altra applicazione.
        if (item.Kind == PayloadKind.Image)
        {
            var png = History.ReadBlob(item);
            if (png == null) return false;
            var uri = IncomingStore.Stage(Android.App.Application.Context, png, item.Id, ".png");
            return uri != null && AndroidClipboard.WriteImage(uri);
        }

        var payload = History.ToPayload(item);
        return payload != null && AndroidClipboard.Write(payload);
    }

    /// <summary>
    /// Preleva i file di un'offerta ricevuta e li mette nel Download del telefono.
    /// Restituisce quanti file sono arrivati a destinazione.
    ///
    /// E' il gemello di <c>TrayContext.MaterializeAsync</c> su Windows, con una
    /// differenza che vale la pena dire: li' i file finiscono in una cartella
    /// dell'applicazione e da li' vanno in clipboard per essere incollati; qui non
    /// c'e' niente in cui incollarli, quindi la destinazione giusta e' il Download
    /// — dove l'utente li cerca, e dove restano anche se l'applicazione sparisce.
    ///
    /// Non si chiama <c>SetMaterialized</c>: quel campo sono percorsi locali veri,
    /// che su Windows servono a non riscaricare. Qui la copia privata viene
    /// buttata appena pubblicata, e scriverci dentro i nomi di Download sarebbe
    /// un percorso che non esiste. A dire che e' fatta basta <c>MarkUsed</c>.
    /// </summary>
    public async Task<int> MaterializeAsync(HistoryItem item, IProgress<FetchProgress>? progress,
                                            CancellationToken ct = default)
    {
        if (item.Kind != PayloadKind.Files) return 0;
        if (item.IsLocalOffer) throw new InvalidOperationException(L.T("msg.originalsGone"));
        if (string.IsNullOrEmpty(item.OwnerId) || string.IsNullOrEmpty(item.OfferId)) return 0;

        var offerId = Guid.Parse(item.OfferId);

        // Cercare il dispositivo e verificare il permesso sono due cose distinte:
        // confonderle faceva dire "non e' in linea" a chi era in linea eccome, ma
        // semplicemente non era accoppiato (stessa lezione del PC).
        var owner = Transport.Peers.FirstOrDefault(p => p.DeviceId == item.OwnerId)
            ?? throw new IOException(L.T("msg.ownerOffline", item.OwnerName));
        if (!owner.Trusted && !Transport.HasAcceptedOffer(owner.DeviceId, offerId))
            throw new IOException(L.T("msg.ownerNotAllowed", item.OwnerName));

        var destDir = Path.Combine(AppConfig.AppDataDir, "received", offerId.ToString("N")[..8]);
        var roots = await Task.Run(() => Transport.FetchAsync(owner, offerId, destDir, ct, progress), ct);
        if (roots.Count == 0) return 0;

        var saved = IncomingStore.PublishToDownloads(Android.App.Application.Context, destDir, roots);
        if (saved > 0) History.MarkUsed(item.Id);

        // La copia privata ha fatto il suo mestiere: i byte ora stanno in Download.
        // Tenerla vorrebbe dire ogni file due volte sul telefono, per sempre.
        try { Directory.Delete(destDir, recursive: true); }
        catch (Exception ex) { Log.Write($"[Android] copia privata non rimossa: {ex.Message}"); }

        return saved;
    }

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
