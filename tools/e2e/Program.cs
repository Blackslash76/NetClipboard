// Banco di prova end-to-end: il codice di rete eseguito davvero, non solo letto.
//
// Fino alla 2.7 tutto il trasporto era stato verificato per costruzione e mai
// messo in moto: handshake, pairing, invio a un non accoppiato, prelievo dei file,
// revoca, presentazioni. Qui nove istanze complete convivono nello stesso processo
// e si parlano su loopback, con le finestre sostituite da risposte scritte in
// codice. Niente tocca i dati dell'applicazione installata.
//
//   dotnet run --project tools/e2e
//
// Esce con codice 1 se un passo fallisce.
using System.Net;
using NetClipboard;
using NetClipboard.Core;
using NetClipboard.E2E;
using NetClipboard.Net;

// Le prove sulla clipboard vera stanno a parte e si chiedono a mano: la clipboard
// e' una sola per sessione, e prendersela mentre qualcuno lavora non si fa.
if (args.Contains("--clipboard"))
    return ClipboardChecks.Run();

var root = Path.Combine(Path.GetTempPath(), "netclip-e2e-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Log.Redirect(Path.Combine(root, "log.txt"));

var ok = 0; var ko = 0;
void Check(string what, bool passed, string detail = "")
{
    Console.WriteLine($"  [{(passed ? "ok " : "KO ")}] {what,-56} {detail}");
    if (passed) ok++; else ko++;
}

// I nodi: ognuno con la propria identita', la propria fiducia e la propria porta.
//   A  il soggetto della prova       B  suo pari fidato, e presentatore
//   C  estraneo, invio accettato     D  estraneo, invio rifiutato
//   E  estraneo, invio di file       R  fidato e poi revocato
//   F/G/H  presentati da B: accettato, rifiutato, lasciato cadere
using var A = new Node("A", root);
using var B = new Node("B", root);
using var C = new Node("C", root);
using var D = new Node("D", root);
using var E = new Node("E", root);
using var R = new Node("R", root);
using var F = new Node("F", root);
using var G = new Node("G", root);
using var H = new Node("H", root);
var all = new[] { A, B, C, D, E, R, F, G, H };

// Tutti dicono di si' al pairing tranne dove serve altrimenti; il SAS mostrato a
// ciascuno resta annotato, cosi' si puo' confrontare fra i due lati.
var sas = new Dictionary<string, string>();
var pairAnswer = all.ToDictionary(n => n.Name, _ => true);
foreach (var n in all)
{
    var me = n;
    me.Transport.PairingConfirm = p =>
    {
        lock (sas) sas[me.Name] = p.Sas;
        return pairAnswer[me.Name];
    };
}

static async Task<bool> Until(Func<bool> cond, int timeoutMs = 5000)
{
    var end = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < end)
    {
        if (cond()) return true;
        await Task.Delay(25);
    }
    return cond();
}

Console.WriteLine("== accoppiamento ==");

var pairAB = await A.PairAsync(B);
Check("A e B si accoppiano", pairAB.Outcome == PairOutcome.Paired, pairAB.Outcome.ToString());
Check("stesso SAS sui due lati", sas.TryGetValue("A", out var sasA) && sas.TryGetValue("B", out var sasB)
        && sasA == sasB && sasA.Length > 0, sas.GetValueOrDefault("A", "—"));
Check("A ha pinnato la chiave di B", A.Trust.Matches(B.DeviceId, B.Identity.PublicKeyDer));
Check("B ha pinnato la chiave di A", B.Trust.Matches(A.DeviceId, A.Identity.PublicKeyDer));

// Un no da una delle due parti non concede niente a nessuno: la fiducia e' un
// atto a due, e mezzo consenso non basta.
pairAnswer["D"] = false;
var pairAD = await A.PairAsync(D);
pairAnswer["D"] = true;
Check("pairing rifiutato da D: nessuna fiducia", pairAD.Outcome == PairOutcome.Rejected
        && !A.Trust.IsTrusted(D.DeviceId) && !D.Trust.IsTrusted(A.DeviceId), pairAD.Outcome.ToString());

Console.WriteLine();
Console.WriteLine("== testo fra dispositivi fidati ==");

B.ClearReceived();
await A.Transport.SendAsync(ClipboardPayload.FromText("ciao dal banco di prova"));
Check("push di testo A -> B", await Until(() => B.Snapshot().Count == 1)
        && B.Snapshot()[0].Payload.Text == "ciao dal banco di prova"
        && !B.Snapshot()[0].FromExternal);

B.ClearReceived();
const string fragment = "<b>grassetto</b> e <i>corsivo</i> con là accentata";
const string rtf = @"{\rtf1\ansi\b grassetto\b0}";
await A.Transport.SendAsync(ClipboardPayload.FromRichText("grassetto e corsivo con là accentata", fragment, rtf));
var rich = await Until(() => B.Snapshot().Count == 1) ? B.Snapshot()[0].Payload : null;
Check("push di testo formattato: HTML e RTF arrivano interi",
    rich != null && rich.Html == fragment && rich.Rtf == rtf && rich.Text!.Contains("là"));

// Le code viaggiano IN FONDO proprio perche' un peer di versione precedente si
// ferma dopo il testo senza guardare se il buffer e' finito. Qui si rifa' a mano
// quella lettura vecchia sugli stessi byte: deve trovare il testo e non inciampare.
{
    var wire = ClipboardPayload.FromRichText("solo testo", fragment, rtf).Serialize();
    using var ms = new MemoryStream(wire);
    using var r = new BinaryReader(ms);
    var kind = r.ReadByte();
    var len = r.ReadInt32();
    var oldText = System.Text.Encoding.UTF8.GetString(r.ReadBytes(len));
    Check("un peer di versione precedente legge il testo e ignora le code",
        kind == (byte)PayloadKind.Text && oldText == "solo testo");
}

Console.WriteLine();
Console.WriteLine("== invio a un dispositivo NON accoppiato ==");

// Ogni estraneo ha il suo mittente: il destinatario tiene una pausa per mittente
// (dieci secondi) perche' nessuno possa tempestarlo di finestre, e con lo stesso
// nodo la seconda prova verrebbe scartata prima di arrivare all'utente.
var offersSeen = new List<IncomingOffer>();
var acceptFrom = new HashSet<string>();
A.Transport.OfferConfirm = o =>
{
    lock (offersSeen) offersSeen.Add(o);
    return acceptFrom.Contains(o.FromDeviceId);
};

await C.PingAsync(A);
await D.PingAsync(A);
await E.PingAsync(A);
Check("gli estranei vedono A senza essere fidati",
    C.PeerOf(A) is { Trusted: false } && D.PeerOf(A) != null && E.PeerOf(A) != null);

A.ClearReceived();
acceptFrom.Add(C.DeviceId);
var sentC = await C.Transport.SendToAsync(C.PeerOf(A)!, ClipboardPayload.FromText("posso lasciarti questo?"));
Check("invio da C accettato: consegnato", sentC == SendOutcome.Delivered, sentC.ToString());
// Il mittente sa di essere stato accettato un istante PRIMA che il contenuto
// compaia dal destinatario: la risposta parte sul filo e solo dopo il clip entra
// nella sua cronologia. Va aspettato, non dato per gia' avvenuto.
Check("A lo segna come contenuto esterno", await Until(() => A.Snapshot().Count == 1)
        && A.Snapshot()[0].FromExternal
        && A.Snapshot()[0].Payload.Text == "posso lasciarti questo?");

A.ClearReceived();
var sentD = await D.Transport.SendToAsync(D.PeerOf(A)!, ClipboardPayload.FromText("e questo?"));
Check("invio da D rifiutato: il mittente lo sa", sentD == SendOutcome.Declined, sentD.ToString());
// Qui si aspetta invece la NON comparsa: mezzo secondo e' piu' del tempo che serve
// al percorso di consegna, che nel caso accettato qui sopra si e' visto chiudersi
// in pochi millisecondi.
await Task.Delay(500);
Check("niente entra in A dopo un rifiuto", A.Snapshot().Count == 0, $"{A.Snapshot().Count} elementi");

Console.WriteLine();
Console.WriteLine("== file veri: offerta, permesso, prelievo ==");

// File con contenuti diversi fra loro, uno dentro una sottocartella: si confrontano
// i byte alla fine, non i nomi.
var srcDir = Path.Combine(root, "sorgente");
Directory.CreateDirectory(Path.Combine(srcDir, "cartella", "dentro"));
var wanted = new Dictionary<string, byte[]>();
foreach (var (rel, size) in new[] { ("cartella/uno.bin", 3), ("cartella/dentro/due.bin", 128 * 1024) })
{
    var bytes = new byte[size];
    Random.Shared.NextBytes(bytes);
    File.WriteAllBytes(Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar)), bytes);
    wanted[rel] = bytes;
}

var offer = FileOffer.FromPaths(new[] { Path.Combine(srcDir, "cartella") }, E.DeviceId, E.Name)!;
E.Offers.Register(offer);

A.ClearReceived();
acceptFrom.Add(E.DeviceId);
var sentE = await E.Transport.SendToAsync(E.PeerOf(A)!, ClipboardPayload.FromOffer(offer));
Check("offerta di file da E accettata", sentE == SendOutcome.Delivered, sentE.ToString());
Check("A ha il permesso di prelevare proprio quell'offerta",
    A.Transport.HasAcceptedOffer(E.DeviceId, offer.OfferId));

var destDir = Path.Combine(root, "scaricati");
var fetched = await A.Transport.FetchAsync(A.PeerOf(E)!, offer.OfferId, destDir, CancellationToken.None);
var identical = wanted.All(kv =>
{
    var p = Path.Combine(destDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
    return File.Exists(p) && File.ReadAllBytes(p).SequenceEqual(kv.Value);
});
Check("i file scaricati sono identici byte per byte", identical && fetched.Count == 1,
    $"{fetched.Count} radice/i");

// Un'offerta mai accettata non si preleva, nemmeno conoscendone l'identificativo.
var stolen = FileOffer.FromPaths(new[] { Path.Combine(srcDir, "cartella") }, E.DeviceId, E.Name)!;
E.Offers.Register(stolen);
var refused = false;
try { await A.Transport.FetchAsync(A.PeerOf(E)!, stolen.OfferId, Path.Combine(root, "mai"), CancellationToken.None); }
catch (IOException) { refused = true; }
Check("prelievo di un'offerta mai accettata: rifiutato", refused);

Console.WriteLine();
Console.WriteLine("== scadenza dei permessi ==");

// Tre minuti non si aspettano: si esercita l'archivio con una finestra breve. Cio'
// che conta e' che la scadenza sia un istante assoluto e sopravviva al riavvio —
// prima era un contatore da accensione, che riparte da zero e vale tutt'altro.
{
    var path = Path.Combine(root, "prova-grants.json");
    var dev = "dispositivo-x"; var id = Guid.NewGuid();
    var store = new GrantStore(path, TimeSpan.FromSeconds(2));
    store.Grant(dev, id);
    Check("permesso appena concesso: valido", store.IsValid(dev, id));
    Check("permesso ritrovato dopo un riavvio", new GrantStore(path, TimeSpan.FromSeconds(2)).IsValid(dev, id));
    store.Revoke(dev, id);
    Check("permesso revocato: non piu' valido", !store.IsValid(dev, id));

    var brief = new GrantStore(Path.Combine(root, "prova-brevi.json"), TimeSpan.FromMilliseconds(300));
    brief.Grant(dev, id);
    await Task.Delay(500);
    Check("permesso scaduto: non piu' valido", !brief.IsValid(dev, id));
}

Console.WriteLine();
Console.WriteLine("== revoca e presentazioni ==");

// R entra nella cerchia di entrambi, poi A lo caccia. Il gossip di B continuera' a
// annunciarlo: e' esattamente il caso in cui la revoca si perdeva.
Check("R accoppiato con A e con B",
    (await A.PairAsync(R)).Outcome == PairOutcome.Paired &&
    (await B.PairAsync(R)).Outcome == PairOutcome.Paired);
A.Trust.Revoke(R.DeviceId);

// F, G, H sono fidati di B soltanto: A li conoscera' solo per presentazione.
foreach (var n in new[] { F, G, H })
    Check($"B accoppiato con {n.Name}", (await B.PairAsync(n)).Outcome == PairOutcome.Paired);

var asked = new List<string>();
var answers = new Dictionary<string, bool?> { ["F"] = true, ["G"] = false, ["H"] = null };
A.Transport.IntroductionConfirm = p =>
{
    lock (asked) asked.Add(p.NewDeviceName);
    return answers.GetValueOrDefault(p.NewDeviceName, false);
};

// Le presenze durano quindici secondi: si rinfrescano prima del giro, altrimenti
// il gossip di B potrebbe non avere piu' nessuno da annunciare.
foreach (var n in new[] { R, F, G, H }) await B.PingAsync(n);
await A.PingAsync(B);

Check("presentato e accettato: F entra nella cerchia", A.Trust.IsTrusted(F.DeviceId));
Check("presentato e rifiutato: G resta fuori e ci resta",
    !A.Trust.IsTrusted(G.DeviceId) && A.Trust.IsRevoked(G.DeviceId));
Check("nessuna risposta: H non entra, ma non e' un no",
    !A.Trust.IsTrusted(H.DeviceId) && !A.Trust.IsRevoked(H.DeviceId));
Check("revocato: R non viene nemmeno riproposto",
    !A.Trust.IsTrusted(R.DeviceId) && !asked.Contains("R"), string.Join(",", asked));

// Secondo giro subito dopo: nessuno dei tre deve tornare a chiedere. F perche' e'
// dentro, G perche' e' una lapide, H perche' la riproposta ha un'attesa di dieci
// minuti — senza quella, il gossip ogni tre secondi diventava un assillo.
var askedBefore = asked.Count;
await A.PingAsync(B);
Check("secondo giro di gossip: nessuna domanda ripetuta", asked.Count == askedBefore,
    $"{askedBefore} domande in tutto");

Console.WriteLine();
foreach (var n in all) n.Dispose();
try { Directory.Delete(root, true); } catch { }

Console.WriteLine($"{ok} controlli superati, {ko} falliti.");
return ko == 0 ? 0 : 1;
