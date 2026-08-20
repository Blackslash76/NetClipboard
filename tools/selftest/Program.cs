// Prove di tenuta sui punti dove un errore costa caro: i parser che leggono dati
// dalla rete e il controllo di identita' del canale locale.
//
// Non serve una seconda macchina: si esercita il codice vero dell'applicazione con
// dati ostili costruiti qui. Da rilanciare dopo ogni modifica a ClipboardPayload,
// FileOffer o InstanceBridge.
//
//   dotnet run --project tools/selftest
using System.Text;
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
Console.WriteLine($"{ok} controlli superati, {ko} falliti.");

return ko == 0 ? 0 : 1;
