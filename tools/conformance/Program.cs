using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetClipboard.Core;
using NetClipboard.Core.Security;

// Banco di conformita' del protocollo.
//
// A cosa serve, adesso che c'e' piu' di una applicazione. Il core e' uno solo e
// lo eseguono tutte, ma "lo stesso codice" smette di essere una garanzia nel
// momento in cui qualcuno lo modifica: basta un campo aggiunto nel punto
// sbagliato perche' un telefono aggiornato non parli piu' con un PC che non lo
// e'. Qui i valori del filo — l'impronta di una chiave, il transcript, la chiave
// di sessione, il SAS, i byte esatti di ogni payload — sono scritti in
// vectors.json e confrontati a ogni compilazione.
//
// Il file e' fatto per essere letto in una differenza: se un cambiamento tocca
// il filo, si vede li', e chi rivede il codice deve dire se e' voluto. Un
// cambiamento voluto si registra con --record; uno non voluto e' un guasto che
// altrimenti si scoprirebbe in casa di chi usa l'applicazione.
//
//   dotnet run --project tools/conformance             verifica (esce 1 se differisce)
//   dotnet run --project tools/conformance -- --record riscrive i valori attesi

var record = args.Contains("--record");
var root = FindRepoRoot() ?? Directory.GetCurrentDirectory();
var path = Path.Combine(root, "tools", "conformance", "vectors.json");

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
};

var vectors = File.Exists(path)
    ? JsonSerializer.Deserialize<Vectors>(File.ReadAllText(path), jsonOpts) ?? new Vectors()
    : new Vectors();

// Gli ingressi si generano una volta sola e non si toccano piu': se cambiassero
// a ogni registrazione, ogni valore atteso cambierebbe con loro e il confronto
// non direbbe piu' niente.
var freshInputs = EnsureInputs(vectors.Inputs);

var computed = Compute(vectors.Inputs);

if (record)
{
    vectors.Expected = computed;
    File.WriteAllText(path, JsonSerializer.Serialize(vectors, jsonOpts) + Environment.NewLine);
    Console.WriteLine($"Registrati {computed.Count} valori in {path}");
    if (freshInputs) Console.WriteLine("Attenzione: gli ingressi erano assenti e sono stati generati adesso.");
    return 0;
}

if (freshInputs)
{
    Console.Error.WriteLine($"Ingressi assenti in {path}: eseguire prima --record.");
    return 1;
}

var failed = 0;
Console.WriteLine("== conformita' del protocollo ==");
foreach (var (key, value) in computed)
{
    var expected = vectors.Expected.GetValueOrDefault(key);
    if (expected == value)
    {
        Console.WriteLine($"  [ok ] {key,-28} {Short(value)}");
        continue;
    }
    failed++;
    Console.WriteLine($"  [NO ] {key,-28}");
    Console.WriteLine($"         atteso  : {Short(expected ?? "(assente)")}");
    Console.WriteLine($"         ottenuto: {Short(value)}");
}

var orphans = vectors.Expected.Keys.Where(k => !computed.ContainsKey(k)).ToList();
foreach (var k in orphans)
{
    failed++;
    Console.WriteLine($"  [NO ] {k,-28} valore atteso che nessun controllo produce piu'");
}

Console.WriteLine();
Console.WriteLine($"{computed.Count - failed} valori coincidono, {failed} no.");
if (failed > 0)
{
    Console.WriteLine();
    Console.WriteLine("Se il cambiamento del filo e' voluto, registrarlo:");
    Console.WriteLine("  dotnet run --project tools/conformance -- --record");
    Console.WriteLine("e far vedere la differenza di vectors.json a chi rivede il codice.");
}
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------- calcoli ---

static Dictionary<string, string> Compute(Inputs inp)
{
    var r = new Dictionary<string, string>(StringComparer.Ordinal);

    using var idA = DeviceIdentity.FromPkcs8(Convert.FromBase64String(inp.IdentityA));
    using var idB = DeviceIdentity.FromPkcs8(Convert.FromBase64String(inp.IdentityB));

    r["identity.deviceIdA"] = idA.DeviceId;
    r["identity.deviceIdB"] = idB.DeviceId;
    r["identity.fingerprintA"] = DeviceIdentity.ShortFingerprint(idA.DeviceId);

    // La firma ECDSA non e' deterministica (k casuale), quindi non si registra la
    // firma: si registra che la verifica riesce, che e' cio' che conta sul filo.
    var signed = idA.Sign(Encoding.UTF8.GetBytes(inp.Text));
    r["identity.verifySelf"] = DeviceIdentity.Verify(idA.PublicKeyDer, Encoding.UTF8.GetBytes(inp.Text), signed).ToString();
    r["identity.verifyOther"] = DeviceIdentity.Verify(idB.PublicKeyDer, Encoding.UTF8.GetBytes(inp.Text), signed).ToString();

    // ----- handshake con le quattro chiavi fissate -----
    var ephA = ECDiffieHellman.Create();
    ephA.ImportPkcs8PrivateKey(Convert.FromBase64String(inp.EphA), out _);
    var ephB = ECDiffieHellman.Create();
    ephB.ImportPkcs8PrivateKey(Convert.FromBase64String(inp.EphB), out _);

    using var hsA = new Handshaker(idA, ephA);
    using var hsB = new Handshaker(idB, ephB);

    var resA = hsA.Complete(idB.PublicKeyDer, hsB.EphPublicKey, selfIsInitiator: true);
    var resB = hsB.Complete(idA.PublicKeyDer, hsA.EphPublicKey, selfIsInitiator: false);

    r["handshake.transcript"] = Convert.ToHexString(resA.Transcript);
    r["handshake.sessionKey"] = Convert.ToHexString(resA.SessionKey);
    r["handshake.sas"] = resA.Sas;
    // I due lati devono arrivare agli stessi numeri: e' tutto cio' che il SAS
    // promette all'utente quando gli chiede di confrontare sei cifre.
    r["handshake.sameTranscript"] = resA.Transcript.SequenceEqual(resB.Transcript).ToString();
    r["handshake.sameKey"] = resA.SessionKey.SequenceEqual(resB.SessionKey).ToString();
    r["handshake.sameSas"] = (resA.Sas == resB.Sas).ToString();
    r["handshake.peerIdSeenByA"] = resA.PeerDeviceId;

    // Le firme sul transcript vanno verificate dall'altro lato, sempre.
    r["handshake.sigVerifies"] =
        Handshaker.VerifyPeer(idB.PublicKeyDer, resA.Transcript, hsB.SignTranscript(resB.Transcript)).ToString();

    // ----- cifrario di sessione -----
    var cipher = new SessionCipher(resA.SessionKey);
    var roundtrip = cipher.Open(cipher.Seal(Encoding.UTF8.GetBytes(inp.Text)));
    r["cipher.roundtrip"] = roundtrip == null ? "(null)" : Encoding.UTF8.GetString(roundtrip);
    // Un blob manomesso non si apre: AES-GCM e' autenticato, e se questo smettesse
    // di valere il canale accetterebbe dati alterati da chi sta in mezzo.
    var tampered = cipher.Seal(Encoding.UTF8.GetBytes(inp.Text));
    tampered[^1] ^= 0x01;
    r["cipher.tamperRejected"] = (cipher.Open(tampered) == null).ToString();

    // ----- payload sul filo: i byte esatti -----
    var text = ClipboardPayload.FromText(inp.Text);
    var rich = ClipboardPayload.FromRichText(inp.Text, inp.Html, inp.Rtf);
    var image = ClipboardPayload.FromImage(Convert.FromBase64String(inp.ImagePng));
    var offer = BuildOffer(inp);
    var files = ClipboardPayload.FromOffer(offer);

    r["payload.text"] = Convert.ToHexString(text.Serialize());
    r["payload.rich"] = Convert.ToHexString(rich.Serialize());
    r["payload.image"] = Convert.ToHexString(image.Serialize());
    r["payload.files"] = Convert.ToHexString(files.Serialize());

    // L'impronta del testo NON guarda la formattazione: e' cio' che impedisce a
    // uno stesso paragrafo copiato da due programmi di comparire due volte in
    // cronologia, ed e' cio' che tiene chiusa la soppressione dell'eco.
    r["hash.text"] = text.ContentHash();
    r["hash.rich"] = rich.ContentHash();
    r["hash.richEqualsText"] = (text.ContentHash() == rich.ContentHash()).ToString();
    r["hash.files"] = files.ContentHash();

    var back = ClipboardPayload.Deserialize(rich.Serialize());
    r["payload.richRoundtrip"] =
        (back.Text == inp.Text && back.Html == inp.Html && back.Rtf == inp.Rtf).ToString();

    // ----- la miniatura in coda all'offerta -----
    var withThumb = BuildOffer(inp);
    withThumb.Thumbnail = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x2A, 0x00, 0x01 }; // non e' un JPEG vero: contano i byte
    var thumbWire = ClipboardPayload.FromOffer(withThumb).Serialize();
    r["payload.filesWithThumb"] = Convert.ToHexString(thumbWire);

    var thumbBack = ClipboardPayload.Deserialize(thumbWire);
    r["payload.thumbRoundtrip"] =
        (thumbBack.Offer!.Thumbnail != null && thumbBack.Offer.Thumbnail.SequenceEqual(withThumb.Thumbnail)).ToString();

    // Un'offerta scritta come la scriveva una versione PRECEDENTE non ha la coda:
    // sono esattamente questi byte meno gli ultimi quattro. Deve continuare a
    // leggersi, altrimenti un telefono aggiornato non parlerebbe piu' con un PC
    // che non lo e'.
    var oldStyle = files.Serialize()[..^4];
    var oldRead = ClipboardPayload.Deserialize(oldStyle);
    r["compat.readsOfferWithoutThumb"] =
        (oldRead.Offer!.Entries.Count == offer.Entries.Count && oldRead.Offer.Thumbnail == null).ToString();

    // ----- le date di modifica: seconda coda dell'offerta -----
    // Valori fissi, mai "adesso": un vettore deve valere anche domani.
    const long t1 = 1_700_000_000_000, t2 = 1_700_000_123_456, t3 = 1_700_000_999_999;

    var dated = BuildOffer(inp);
    dated.Entries[0].ModifiedUnixMs = t1;
    dated.Entries[1].ModifiedUnixMs = t2;
    dated.Entries[2].ModifiedUnixMs = t3;
    var datedWire = ClipboardPayload.FromOffer(dated).Serialize();
    r["payload.filesDated"] = Convert.ToHexString(datedWire);

    var datedBack = ClipboardPayload.Deserialize(datedWire);
    r["payload.datedRoundtrip"] =
        (datedBack.Offer!.Entries.Count == dated.Entries.Count &&
         datedBack.Offer.Entries[1].ModifiedUnixMs == t2).ToString();

    r["hash.filesDated"] = ClipboardPayload.FromOffer(dated).ContentHash();
    r["hash.datedDiffersFromUndated"] =
        (ClipboardPayload.FromOffer(dated).ContentHash() != files.ContentHash()).ToString();

    // Il motivo per cui la data e' stata aggiunta: due file con lo stesso nome e
    // la stessa dimensione, ma modificati in momenti diversi, non sono lo stesso
    // contenuto. Senza la data avevano la stessa impronta.
    var edited = BuildOffer(inp);
    edited.Entries[0].ModifiedUnixMs = t1;
    edited.Entries[1].ModifiedUnixMs = t2;
    edited.Entries[2].ModifiedUnixMs = 1_888_888_888_888;   // solo questa cambia
    r["hash.datedSeparatesEditedFile"] =
        (ClipboardPayload.FromOffer(edited).ContentHash() !=
         ClipboardPayload.FromOffer(dated).ContentHash()).ToString();

    // La coda e' in APPENDA: i byte dell'offerta senza date sono un prefisso
    // esatto di quelli con le date. E' cio' che permette a un lettore di una
    // versione precedente di fermarsi prima e non accorgersi di niente.
    var plainWire = files.Serialize();
    r["compat.datedIsAppendOnly"] =
        (datedWire.Length > plainWire.Length &&
         datedWire.AsSpan(0, plainWire.Length).SequenceEqual(plainWire)).ToString();

    // E il verso opposto, dal vivo: un'offerta CON le date, troncata a quanto ne
    // leggerebbe chi si aspetta solo la miniatura, deve restare valida \u2014 con le
    // date a zero, cioe' "non note", non con uno zero che finge di essere una data.
    var truncRead = ClipboardPayload.Deserialize(datedWire[..plainWire.Length]);
    r["compat.datedReadWithoutDates"] =
        (truncRead.Offer!.Entries.Count == dated.Entries.Count &&
         truncRead.Offer.Entries.All(e => e.ModifiedUnixMs == 0)).ToString();

    var backFiles = ClipboardPayload.Deserialize(files.Serialize());
    r["payload.filesRoundtrip"] =
        (backFiles.Offer!.OfferId == offer.OfferId &&
         backFiles.Offer.Entries.Count == offer.Entries.Count &&
         backFiles.Offer.Entries[0].RelativePath == offer.Entries[0].RelativePath).ToString();

    // ----- compatibilita' fra versioni, nei due versi -----

    // Verso il passato: i byte che manderebbe una 2.7 (solo testo, nessuna coda)
    // devono continuare a leggersi.
    r["compat.readsPlainFromOld"] = ClipboardPayload.Deserialize(text.Serialize()).Text ?? "(null)";

    // Verso il futuro: una coda con un'etichetta che questa versione non conosce
    // si salta e non fa perdere niente di cio' che viene prima. E' la promessa
    // che permettera' di aggiungere un formato senza rompere le versioni in giro.
    r["compat.skipsUnknownTail"] = ClipboardPayload.Deserialize(WithUnknownTail(inp)).Text ?? "(null)";

    // ----- CF_HTML: gli scarti sono in byte, non in caratteri -----
    var fragment = inp.Html;
    var cf = CfHtml.Build(fragment);
    r["cfhtml.built"] = cf;
    r["cfhtml.roundtrip"] = (CfHtml.ExtractFragment(cf) == fragment).ToString();
    // Un accento prima del frammento sposta tutto di un byte: se questo controllo
    // cade, chi incolla si ritrova mezzo tag.
    var accented = CfHtml.Build("àèìòù <b>" + fragment + "</b>");
    r["cfhtml.accentedRoundtrip"] = (CfHtml.ExtractFragment(accented) == "àèìòù <b>" + fragment + "</b>").ToString();

    // ----- forziere locale: un blob registrato deve restare apribile -----
    var vaultDir = Path.Combine(Path.GetTempPath(), "netclip-conformance");
    Directory.CreateDirectory(vaultDir);
    var keyPath = Path.Combine(vaultDir, "history.key");
    File.WriteAllBytes(keyPath, Convert.FromBase64String(inp.VaultKey));
    var vault = new LocalVault(keyPath, PlainProtector.Instance);
    var opened = vault.Open(Convert.FromBase64String(inp.SealedFixture));
    r["vault.opensFixture"] = opened == null ? "(null)" : Encoding.UTF8.GetString(opened);
    // Un file scritto da una versione che non cifrava si rilegge com'e': e' la
    // migrazione della cronologia, e vale finche' non viene riscritto.
    var plain = Encoding.UTF8.GetBytes("cronologia di una versione precedente");
    r["vault.readsPlainLegacy"] = Encoding.UTF8.GetString(vault.Open(plain)!);
    r["vault.sealedIsRecognised"] = LocalVault.IsSealed(vault.Seal(plain)).ToString();

    return r;
}

/// <summary>Un payload testo con, in mezzo alle code, un'etichetta che questa versione non conosce.</summary>
static byte[] WithUnknownTail(Inputs inp)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
    w.Write((byte)1); // PayloadKind.Text
    var body = Encoding.UTF8.GetBytes(inp.Text);
    w.Write(body.Length); w.Write(body);

    var future = Encoding.UTF8.GetBytes("formato inventato da una versione futura");
    w.Write((byte)99); w.Write(future.Length); w.Write(future);

    var html = Encoding.UTF8.GetBytes(inp.Html);
    w.Write((byte)1); w.Write(html.Length); w.Write(html);
    w.Flush();
    return ms.ToArray();
}

static FileOffer BuildOffer(Inputs inp) => new()
{
    OfferId = Guid.Parse(inp.OfferId),
    OwnerDeviceId = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0",
    OwnerName = "PC-DI-PROVA",
    Entries =
    {
        new FileEntry { RootIndex = 0, IsDir = true, Size = 0, RelativePath = "cartella" },
        new FileEntry { RootIndex = 0, IsDir = false, Size = 1234, RelativePath = "cartella/relazione àè.txt" },
        new FileEntry { RootIndex = 1, IsDir = false, Size = 7, RelativePath = "nota.txt" },
    },
};

// ---------------------------------------------------------------- ingressi ---

/// <summary>Genera cio' che manca e dice se ha dovuto farlo. Cio' che c'e' non si tocca.</summary>
static bool EnsureInputs(Inputs inp)
{
    var generated = false;

    if (string.IsNullOrEmpty(inp.IdentityA)) { inp.IdentityA = NewEcdsa(); generated = true; }
    if (string.IsNullOrEmpty(inp.IdentityB)) { inp.IdentityB = NewEcdsa(); generated = true; }
    if (string.IsNullOrEmpty(inp.EphA)) { inp.EphA = NewEcdh(); generated = true; }
    if (string.IsNullOrEmpty(inp.EphB)) { inp.EphB = NewEcdh(); generated = true; }
    if (string.IsNullOrEmpty(inp.VaultKey)) { inp.VaultKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); generated = true; }
    if (string.IsNullOrEmpty(inp.OfferId)) { inp.OfferId = Guid.NewGuid().ToString(); generated = true; }

    if (string.IsNullOrEmpty(inp.SealedFixture))
    {
        // Il nonce di AES-GCM e' casuale, quindi sigillare non e' un'operazione
        // riproducibile: il blob si registra una volta e da li' in avanti si
        // verifica che continui ad APRIRSI. E' il verso che conta — un formato
        // cambiato rende illeggibile la cronologia gia' scritta.
        var dir = Path.Combine(Path.GetTempPath(), "netclip-conformance-seed");
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "history.key");
        File.WriteAllBytes(keyPath, Convert.FromBase64String(inp.VaultKey));
        var vault = new LocalVault(keyPath, PlainProtector.Instance);
        inp.SealedFixture = Convert.ToBase64String(vault.Seal(Encoding.UTF8.GetBytes(inp.Text)));
        generated = true;
    }

    return generated;
}

static string NewEcdsa()
{
    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    return Convert.ToBase64String(k.ExportPkcs8PrivateKey());
}

static string NewEcdh()
{
    using var k = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    return Convert.ToBase64String(k.ExportPkcs8PrivateKey());
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "NetClipboard.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

static string Short(string s) => s.Length <= 64 ? s : s[..61] + "...";

// ----------------------------------------------------------------- modello ---

sealed class Vectors
{
    public Inputs Inputs { get; set; } = new();
    public Dictionary<string, string> Expected { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Gli ingressi del banco. Chiavi di prova e basta: sono in chiaro in un file
/// versionato di proposito, e non proteggono niente.
/// </summary>
sealed class Inputs
{
    public string IdentityA { get; set; } = "";
    public string IdentityB { get; set; } = "";
    public string EphA { get; set; } = "";
    public string EphB { get; set; } = "";
    public string VaultKey { get; set; } = "";
    public string SealedFixture { get; set; } = "";
    public string OfferId { get; set; } = "";

    // Testo con accenti, caratteri fuori dal piano latino, a capo Windows e un
    // emoji: se una lunghezza venisse mai misurata in caratteri invece che in
    // byte, si vedrebbe qui e non in casa di chi usa l'applicazione.
    public string Text { get; set; } = "Ciao, mondo! àèìòù 日本語 🙂\r\nseconda riga";
    public string Html { get; set; } = "<b>ciao</b> àèì";
    public string Rtf { get; set; } = "{\\rtf1\\ansi ciao}";

    /// <summary>Non e' un PNG vero: al protocollo interessano i byte, non l'immagine.</summary>
    public string ImagePng { get; set; } = Convert.ToBase64String(new byte[]
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03,
    });
}

/// <summary>
/// Il custode dei segreti del banco: non custodisce niente. Va benissimo qui —
/// la chiave e' scritta in chiaro nei vettori e serve solo a rendere il formato
/// del forziere riproducibile — e non deve esistere da nessun'altra parte.
/// </summary>
sealed class PlainProtector : ISecretProtector
{
    public static readonly PlainProtector Instance = new();

    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[]? Unprotect(byte[] wrapped) => wrapped;
}
