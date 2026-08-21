// Prove di tenuta sui punti dove un errore costa caro: i parser che leggono dati
// dalla rete e il controllo di identita' del canale locale.
//
// Non serve una seconda macchina: si esercita il codice vero dell'applicazione con
// dati ostili costruiti qui. Da rilanciare dopo ogni modifica a ClipboardPayload,
// FileOffer o InstanceBridge.
//
//   dotnet run --project tools/selftest
using System.Text;
using NetClipboard;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Platform;

var ok = 0; var ko = 0;
void Check(string what, bool passed, string detail = "")
{
    Console.WriteLine($"  [{(passed ? "ok " : "KO ")}] {what,-52} {detail}");
    if (passed) ok++; else ko++;
}

Console.WriteLine("== traffico normale: deve continuare a funzionare ==");

var text = ClipboardPayload.FromText(new string('x', 300_000));
var back = ClipboardPayload.Deserialize(text.Serialize());
Check("testo lungo 300.000 caratteri", back.Text == text.Text);

var png = new byte[512 * 1024];
Random.Shared.NextBytes(png);
var img = ClipboardPayload.Deserialize(ClipboardPayload.FromImage(png).Serialize());
Check("immagine da 512 KB", img.ImagePng!.SequenceEqual(png));

var offer = new FileOffer { OfferId = Guid.NewGuid(), OwnerDeviceId = "abc", OwnerName = "PC" };
for (var i = 0; i < 2_000; i++)
    offer.Entries.Add(new FileEntry { RootIndex = i, Size = i, RelativePath = $"cartella/file-{i}.txt" });
var got = ClipboardPayload.Deserialize(ClipboardPayload.FromOffer(offer).Serialize()).Offer!;
Check("offerta con 2.000 voci", got.Entries.Count == 2_000 && got.Entries[1999].RelativePath == "cartella/file-1999.txt");

var formatted = ClipboardPayload.FromRichText("testo", "<b>ciao</b>", @"{\rtf1 ciao}");
var backRich = ClipboardPayload.Deserialize(formatted.Serialize());
Check("testo con HTML e RTF in coda",
    backRich.Text == "testo" && backRich.Html == "<b>ciao</b>" && backRich.Rtf == @"{\rtf1 ciao}");

// Le code sono facoltative e chi legge non sa quante ne troverà: una di tipo
// sconosciuto — una versione futura — si salta e si va avanti.
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    var t = Encoding.UTF8.GetBytes("testo");
    w.Write((byte)1); w.Write(t.Length); w.Write(t);
    w.Write((byte)99); w.Write(3); w.Write(new byte[] { 1, 2, 3 });   // coda mai vista
    var h = Encoding.UTF8.GetBytes("<i>x</i>");
    w.Write((byte)1); w.Write(h.Length); w.Write(h);                  // e dopo, una che si conosce
    w.Flush();
    var got2 = ClipboardPayload.Deserialize(ms.ToArray());
    Check("coda di tipo sconosciuto: saltata, non fatale", got2.Text == "testo" && got2.Html == "<i>x</i>");
}

// L'HTML che Word mette in clipboard arriva a megabyte per un paragrafo: oltre il
// tetto si degrada al testo semplice invece di gonfiare ogni invio.
var huge = new string('a', ClipboardPayload.MaxRichBytes + 1);
Check("HTML oltre il tetto: si degrada al testo",
    ClipboardPayload.FromRichText("testo", huge, null).Html == null);

Console.WriteLine();
Console.WriteLine("== CF_HTML: gli scarti si contano in byte ==");

// La trappola del formato: StartFragment/EndFragment sono scarti in BYTE sulla
// codifica UTF-8. Con un frammento pieno di accenti e simboli, chi li contasse in
// caratteri taglierebbe a meta' un tag, e chi incolla se ne accorgerebbe subito.
{
    const string frag = "<p>perché però €20 — città</p>";
    var cf = CfHtml.Build(frag);
    Check("frammento ricostruito identico", CfHtml.ExtractFragment(cf) == frag);

    var declared = int.Parse(cf.Split("EndFragment:")[1][..10]);
    Check("EndFragment cade sul byte giusto", declared == Encoding.UTF8.GetByteCount(cf.Split("<!--EndFragment-->")[0]),
        $"dichiarato {declared}");

    // Scarti incoerenti (capita, e non e' colpa di chi incolla): si ripiega sui
    // commenti invece di restituire spazzatura.
    var broken = cf.Replace("StartFragment:", "StartFragmenX:");
    Check("scarti illeggibili: si ripiega sui commenti", CfHtml.ExtractFragment(broken) == frag);
}

Console.WriteLine();
Console.WriteLine("== cronologia cifrata a riposo ==");

{
    var dir = Path.Combine(Path.GetTempPath(), "netclip-vault-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    var vault = new LocalVault(Path.Combine(dir, "history.key"), WindowsSecretProtector.Instance);

    var secret = Encoding.UTF8.GetBytes("password: non deve stare in chiaro su disco");
    var sealedBytes = vault.Seal(secret);
    Check("il testo in chiaro non compare nel blob",
        !Convert.ToHexString(sealedBytes).Contains(Convert.ToHexString(secret)));
    Check("blob riaperto identico", vault.Open(sealedBytes)!.SequenceEqual(secret));
    Check("la stessa chiave si ritrova dopo un riavvio",
        new LocalVault(Path.Combine(dir, "history.key"), WindowsSecretProtector.Instance).Open(sealedBytes)!.SequenceEqual(secret));

    // File di una versione precedente: niente firma, si rileggono com'erano.
    Check("file in chiaro di prima: ancora leggibile", vault.Open(secret)!.SequenceEqual(secret));

    // Blob manomesso: AES-GCM non lo apre, e non si finge di poterlo fare.
    var tampered = sealedBytes.ToArray();
    tampered[^1] ^= 0xFF;
    Check("blob manomesso: rifiutato", vault.Open(tampered) == null);

    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine("== dati ostili: devono essere rifiutati senza allocare ==");

static byte[] Hostile(int declared)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((byte)1); w.Write(declared); w.Write(new byte[4]);
    w.Flush(); return ms.ToArray();
}
static byte[] HostileOffer(int entries)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((byte)3); w.Write(Guid.NewGuid().ToByteArray()); w.Write(0); w.Write(0); w.Write(entries);
    w.Flush(); return ms.ToArray();
}

foreach (var declared in new[] { 600 * 1024 * 1024, int.MaxValue })
{
    var data = Hostile(declared);
    var before = GC.GetTotalAllocatedBytes();
    var rejected = false;
    try { ClipboardPayload.Deserialize(data); } catch (InvalidDataException) { rejected = true; }
    var mb = (GC.GetTotalAllocatedBytes() - before) / 1048576.0;
    Check($"9 byte che dichiarano {declared / 1048576} MB", rejected && mb < 1, $"allocati {mb:0.00} MB");
}

foreach (var n in new[] { 10_000_000, int.MaxValue })
{
    var data = HostileOffer(n);
    var rejected = false;
    try { ClipboardPayload.Deserialize(data); } catch (InvalidDataException) { rejected = true; }
    Check($"offerta che dichiara {n} voci", rejected);
}

// Le code del testo arrivano dalla rete come tutto il resto: anche la loro
// lunghezza va confrontata con lo spazio reale, o il buco si riapre da li'.
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    var t = Encoding.UTF8.GetBytes("ok");
    w.Write((byte)1); w.Write(t.Length); w.Write(t);
    w.Write((byte)1); w.Write(600 * 1024 * 1024); w.Write(new byte[4]);
    w.Flush();
    var data = ms.ToArray();
    var before = GC.GetTotalAllocatedBytes();
    var rejected = false;
    try { ClipboardPayload.Deserialize(data); } catch (InvalidDataException) { rejected = true; }
    var mb = (GC.GetTotalAllocatedBytes() - before) / 1048576.0;
    Check("coda HTML che dichiara 600 MB", rejected && mb < 1, $"allocati {mb:0.00} MB");
}

Console.WriteLine();
Console.WriteLine("== identita' del chiamante sulla pipe ==");
// Non si usa la pipe vera: quella e' occupata dall'app in esecuzione, e una
// prova non deve andare a bussare al programma di chi lavora. Si ripete qui
// lo stesso meccanismo: il client concede l'impersonation, il servente legge
// il SID e lo confronta con il proprio.
{
    // Un giro per volta, con una stretta di mano in mezzo. Prima la prova
    // dipendeva da chi arrivava primo: se il client si connetteva e si
    // disconnetteva mentre il servente non era ancora in ascolto,
    // WaitForConnection sollevava, il task moriva e il giro dopo bussava a
    // nessuno. Sul PC di chi sviluppa non si vedeva; sotto carico — cioe' in CI —
    // cadeva a caso, ed e' il modo migliore per far ignorare una build rossa.
    //
    // Ora: il servente dichiara di essere in ascolto (listening), e il client
    // resta connesso finche' il servente non ha finito di guardarlo.
    bool Identified(System.Security.Principal.TokenImpersonationLevel level)
    {
        var name = "netclip-test-" + Guid.NewGuid().ToString("N");
        var listening = new ManualResetEventSlim(false);
        var identified = false;

        var server = Task.Run(() =>
        {
            using var srv = new System.IO.Pipes.NamedPipeServerStream(
                name, System.IO.Pipes.PipeDirection.In, 1);
            listening.Set();
            srv.WaitForConnection();
            try
            {
                System.Security.Principal.SecurityIdentifier? sid = null;
                srv.RunAsClient(() => sid = System.Security.Principal.WindowsIdentity.GetCurrent().User);
                using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
                identified = sid != null && me.User != null && sid.Equals(me.User);
            }
            catch
            {
                identified = false; // il client non ha concesso l'identita': e' il caso che ci interessa
            }
        });

        listening.Wait(TimeSpan.FromSeconds(15));
        using (var client = new System.IO.Pipes.NamedPipeClientStream(".", name,
                   System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.None, level))
        {
            client.Connect(15000);
            server.Wait(TimeSpan.FromSeconds(20)); // dentro il using: il servente lo trova ancora attaccato
        }
        return identified;
    }

    Check("client che concede l'identita': riconosciuto",
        Identified(System.Security.Principal.TokenImpersonationLevel.Impersonation));
    Check("client che non la concede: rifiutato",
        !Identified(System.Security.Principal.TokenImpersonationLevel.None));
}

Console.WriteLine();
Console.WriteLine("== spazio e conservazione: cio' che scriviamo deve anche sparire ==");

// Ogni regola qui sotto esisteva gia' da qualche parte nel codice. Il problema non
// era la regola: era che non la eseguiva nessuno, o che copriva la cosa sbagliata.
// Provate qui, non possono tornare a marcire in silenzio.
{
    var root = Path.Combine(Path.GetTempPath(), "netclipboard-selftest-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);

    // ----- il log gira anche mentre l'applicazione sta su -----
    // Prima il tetto si guardava solo in Log.Start(): un processo che non si
    // riavvia — il servizio in primo piano di Android — non lo raggiungeva mai.
    var logPath = Path.Combine(root, "log.txt");
    Log.Redirect(logPath);
    var previous = Path.Combine(root, "log.prev.txt");

    var line = new string('x', 900);
    for (var i = 0; i < 1500; i++) Log.Write(line);   // ~1,35 MB, oltre il tetto di 1 MB

    Check("log oltre il tetto: gira senza riavviare", File.Exists(previous));
    Check("log nuovo ripartito piccolo",
        File.Exists(logPath) && new FileInfo(logPath).Length < 1024 * 1024,
        File.Exists(logPath) ? $"{new FileInfo(logPath).Length / 1024} KB" : "assente");

    // Due generazioni e non di piu': e' il patto che questa cartella non cresca.
    for (var i = 0; i < 1500; i++) Log.Write(line);
    var total = new[] { logPath, previous }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
    Check("due giri di rotazione: al massimo due file", total <= 2 * 1024 * 1024 + 65536,
        $"{total / 1024} KB in tutto");

    // ----- la cronologia ha un tetto in BYTE, non solo in voci -----
    var stateDir = Path.Combine(root, "stato");
    Directory.CreateDirectory(stateDir);
    AppConfig.UseAppDataDir(stateDir);
    var config = AppConfig.Load();

    ClipboardPayload Image(int kb)
    {
        var bytes = new byte[kb * 1024];
        Random.Shared.NextBytes(bytes);
        return ClipboardPayload.FromImage(bytes);
    }

    long BlobBytesOnDisk() => new DirectoryInfo(Path.Combine(stateDir, "history"))
        .EnumerateFiles("*.png").Sum(f => f.Length);

    const long budget = 1024 * 1024;   // 1 MB
    {
        var history = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);
        for (var i = 0; i < 6; i++) history.Add(Image(400), "prova", isLocal: true);

        // Sei immagini da 400 KB sono 2,4 MB: HistorySize (30) non le avrebbe
        // toccate, perche' conta le voci. Il tetto in byte si'.
        Check("sei immagini da 400 KB rientrano nel tetto di 1 MB",
            BlobBytesOnDisk() <= budget, $"{BlobBytesOnDisk() / 1024} KB su disco");
        Check("le voci in eccesso sono state tolte davvero", history.Items.Count < 6,
            $"{history.Items.Count} voci rimaste");
    }

    // ----- il pin vince sul tetto -----
    {
        Directory.Delete(Path.Combine(stateDir, "history"), recursive: true);
        var history = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);
        var first = history.Add(Image(400), "prova", isLocal: true);
        history.TogglePin(first.Id);
        for (var i = 0; i < 6; i++) history.Add(Image(400), "prova", isLocal: true);

        Check("un'immagine con il pin non viene sacrificata",
            history.Items.Any(i => i.Id == first.Id && i.Pinned));
    }

    // ----- gli allegati orfani vengono raccolti -----
    // Il blob si scrive PRIMA che l'indice venga salvato: se il processo muore in
    // mezzo, quel file non lo nomina piu' nessuno. Su un telefono e' il caso
    // normale, non l'incidente.
    var historyDir = Path.Combine(stateDir, "history");
    {
        Directory.Delete(historyDir, recursive: true);
        var history = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);
        var kept = history.Add(Image(50), "prova", isLocal: true);
        var keptBlob = Path.Combine(historyDir, kept.BlobFile!);

        var orphan = Path.Combine(historyDir, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(orphan, new byte[64 * 1024]);

        _ = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);   // riapertura = spazzata
        Check("allegato che l'indice non nomina: rimosso", !File.Exists(orphan));
        Check("allegato ancora in uso: NON rimosso", File.Exists(keptBlob));
    }

    // ----- con l'indice illeggibile non si spazza -----
    // Senza indice la lista resta vuota, e spazzare vorrebbe dire cancellare tutto
    // basandosi su un elenco che non e' arrivato: sarebbe giusto per caso.
    {
        Directory.Delete(historyDir, recursive: true);
        var history = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);
        var item = history.Add(Image(50), "prova", isLocal: true);
        var blob = Path.Combine(historyDir, item.BlobFile!);

        File.WriteAllBytes(Path.Combine(historyDir, "history.dat"), new byte[] { 1, 2, 3, 4, 5 });
        _ = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);
        Check("indice illeggibile: gli allegati restano dove sono", File.Exists(blob));
    }

    // ----- ricondividere rimette in circolo -----
    // Un trasferimento ricevuto si consuma incollandolo, e la riga resta come
    // traccia. Ma se lo stesso contenuto torna, e' un passaggio di consegne NUOVO:
    // la voce deve tornare in testa e tornare utilizzabile.
    {
        Directory.Delete(historyDir, recursive: true);
        var history = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);

        FileOffer OfferOf(Guid id) => new()
        {
            OfferId = id,
            OwnerDeviceId = "telefono",
            OwnerName = "Pixel",
            Entries = { new FileEntry { RootIndex = 0, Size = 1234, RelativePath = "relazione.pdf" } },
        };

        var first = OfferOf(Guid.NewGuid());
        var item = history.Add(ClipboardPayload.FromOffer(first), "Pixel", isLocal: false);
        history.MarkUsed(item.Id);
        Check("trasferimento incollato: segnato come usato",
            ClipboardHistory.IsSpent(history.GetById(item.Id)!));

        // Qualcosa d'altro in mezzo, cosi' la voce non e' piu' in testa.
        history.Add(ClipboardPayload.FromText("altro"), "PC", isLocal: true);

        // Stessa condivisione rifatta dal telefono: stessi file, offerta NUOVA.
        var second = OfferOf(Guid.NewGuid());
        var again = history.Add(ClipboardPayload.FromOffer(second), "Pixel", isLocal: false);

        Check("ricondiviso: ricicla la voce invece di crearne un'altra",
            again.Id == item.Id && history.Items.Count(i => i.Kind == PayloadKind.Files) == 1);
        Check("ricondiviso: torna utilizzabile", !ClipboardHistory.IsSpent(again));
        Check("ricondiviso: torna in testa all'elenco", history.Items[0].Id == item.Id);

        // La meta' meno visibile: la riga rianimata deve puntare all'offerta NUOVA,
        // altrimenti sembra utilizzabile e poi si scarica a vuoto.
        Check("ricondiviso: punta alla nuova offerta, non a quella morta",
            again.OfferId == second.OfferId.ToString("N"),
            again.OfferId == first.OfferId.ToString("N") ? "punta ancora alla vecchia!" : "");

        // Due file DIVERSI con lo stesso nome e la stessa dimensione hanno la
        // stessa impronta: finiscono sulla stessa voce. Non e' bello, ma non deve
        // essere pericoloso — la voce non puo' continuare a puntare ai byte
        // scaricati la volta prima.
        history.SetMaterialized(again.Id, new List<string> { @"C:\scaricati\relazione.pdf" });
        var terzo = OfferOf(Guid.NewGuid());
        var dopo = history.Add(ClipboardPayload.FromOffer(terzo), "Pixel", isLocal: false);
        Check("stessa impronta, offerta nuova: si riscarica invece di servire i byte vecchi",
            dopo.LocalRootPaths == null || dopo.LocalRootPaths.Count == 0,
            dopo.LocalRootPaths is { Count: > 0 } ? "punta ancora al vecchio scaricato!" : "");

        // E deve reggere anche dopo un riavvio: lo stato sta su disco.
        var reopened = new ClipboardHistory(config, WindowsSecretProtector.Instance, budget);
        var persisted = reopened.GetById(item.Id);
        Check("ricondiviso: lo stato sopravvive alla riapertura",
            persisted != null && !ClipboardHistory.IsSpent(persisted) &&
            persisted.OfferId == terzo.OfferId.ToString("N"));

        // E il rovescio: se il file e' stato MODIFICATO, non e' piu' lo stesso
        // contenuto, e non deve finire sulla stessa voce. Con la sola coppia
        // nome+dimensione erano indistinguibili; e' per questo che l'offerta
        // porta anche la data.
        var prima = reopened.Items.Count;
        var modificato = OfferOf(Guid.NewGuid());
        modificato.Entries[0].ModifiedUnixMs = 1_700_000_000_000;
        var voceA = reopened.Add(ClipboardPayload.FromOffer(modificato), "Pixel", isLocal: false);

        var ancoraModificato = OfferOf(Guid.NewGuid());
        ancoraModificato.Entries[0].ModifiedUnixMs = 1_900_000_000_000;   // stesso nome, stessa misura, altra data
        var voceB = reopened.Add(ClipboardPayload.FromOffer(ancoraModificato), "Pixel", isLocal: false);

        Check("stesso nome e misura ma data diversa: due contenuti, non uno",
            voceA.Id != voceB.Id && reopened.Items.Count == prima + 2);

        // E due condivisioni della stessa identica cosa restano una sola voce.
        var identico = OfferOf(Guid.NewGuid());
        identico.Entries[0].ModifiedUnixMs = 1_900_000_000_000;
        var voceC = reopened.Add(ClipboardPayload.FromOffer(identico), "Pixel", isLocal: false);
        Check("stessa data: continua a riciclare la voce", voceC.Id == voceB.Id);
    }

    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{ok} controlli superati, {ko} falliti.");

return ko == 0 ? 0 : 1;
