using System.IO;
using System.Text;

namespace NetClipboard.Core;

/// <summary>
/// Una voce dell'offerta file: file o cartella, con percorso relativo alla
/// radice trascinata. Solo metadati, mai byte.
/// </summary>
public sealed class FileEntry
{
    /// <summary>Indice della radice (top-level) a cui appartiene la voce.</summary>
    public int RootIndex { get; set; }
    public bool IsDir { get; set; }
    public long Size { get; set; }

    /// <summary>Percorso relativo con separatore '/', include il nome della radice come primo segmento.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>
    /// Ultima modifica, in millisecondi dall'epoca Unix, UTC. <b>Zero = non nota</b>
    /// (mittente di versione precedente, o file di cui non si e' potuta leggere).
    ///
    /// Non serve a mostrare una data: serve a <see cref="ClipboardPayload.ContentHash"/>.
    /// L'impronta di un'offerta non puo' guardare i byte, perche' i byte non sono
    /// ancora viaggiati — e' tutto il senso del prelievo differito — quindi guarda
    /// i metadati. Con soli nome e dimensione, due file diversi ma della stessa
    /// misura risultano lo stesso contenuto; con la data si distinguono.
    ///
    /// Millisecondi Unix e non <c>DateTime.Ticks</c>: e' un formato di filo, e
    /// deve poterlo scrivere anche chi non programma in .NET.
    /// </summary>
    public long ModifiedUnixMs { get; set; }
}

/// <summary>
/// "Segnaposto" di una copia di file/cartelle (delayed rendering): descrive cosa
/// e' disponibile e da chi, senza trasferire i byte. I byte viaggiano solo quando
/// il destinatario incolla/materializza, via ClipboardTransport.FetchAsync.
///
/// I campi Root* sono lato-host (chi possiede i file) e NON viaggiano sul filo:
/// servono a risolvere il percorso assoluto in fase di streaming.
/// </summary>
public sealed class FileOffer
{
    public const int MaxEntries = 50_000;

    /// <summary>Byte minimi di una voce sul filo: indice, flag, dimensione, lunghezza del nome.</summary>
    private const int MinEntryBytes = 4 + 1 + 8 + 4;

    public Guid OfferId { get; set; }
    public string OwnerDeviceId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public List<FileEntry> Entries { get; set; } = new();

    /// <summary>Cartella padre di ciascuna radice (lato host, non serializzato).</summary>
    public List<string>? RootParents { get; set; }

    /// <summary>
    /// Miniatura di cio' che l'offerta contiene (JPEG o PNG), se il mittente ha
    /// potuto farne una. Facoltativa, e piccola.
    ///
    /// Serve perche' nel rendering ritardato chi riceve ha solo nomi e dimensioni
    /// finche' non scarica: di una foto vedeva il nome e nient'altro, e doveva
    /// scaricarla per sapere cos'era. La miniatura fa vedere COSA si sta per
    /// prendere, prima di prenderlo.
    ///
    /// Viaggia <b>in coda</b> all'offerta, per la regola di §"come si cambia il
    /// protocollo": un peer di versione precedente si ferma dopo le voci e non
    /// se ne accorge.
    /// </summary>
    public byte[]? Thumbnail { get; set; }

    /// <summary>
    /// Tetto della miniatura. Una 256x256 in JPEG sta sotto i 20 KB; oltre questo
    /// limite non e' piu' una miniatura, ed e' un modo per gonfiare ogni offerta.
    /// </summary>
    public const int MaxThumbnailBytes = 64 * 1024;

    public IEnumerable<string> TopLevelNames =>
        Entries.Where(e => !e.RelativePath.Contains('/'))
               .Select(e => e.RelativePath)
               .Distinct();

    public int FileCount => Entries.Count(e => !e.IsDir);
    public int DirCount => Entries.Count(e => e.IsDir);
    public long TotalSize => Entries.Where(e => !e.IsDir).Sum(e => e.Size);

    /// <summary>Risolve il percorso assoluto locale di una voce (solo lato host).</summary>
    public string? ResolveLocal(FileEntry e)
    {
        if (RootParents == null || e.RootIndex < 0 || e.RootIndex >= RootParents.Count)
            return null;
        var rel = e.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(RootParents[e.RootIndex], rel);
    }

    // ----- Serializzazione binaria (per il payload sul filo) -----

    public void WriteTo(BinaryWriter w)
    {
        w.Write(OfferId.ToByteArray());
        WriteString(w, OwnerDeviceId);
        WriteString(w, OwnerName);
        w.Write(Entries.Count);
        foreach (var e in Entries)
        {
            w.Write(e.RootIndex);
            w.Write(e.IsDir);
            w.Write(e.Size);
            WriteString(w, e.RelativePath);
        }

        // Coda facoltativa: si scrive sempre la lunghezza (anche zero), cosi' il
        // formato resta regolare. Chi legge con una versione precedente si ferma
        // dopo le voci e ignora questi byte.
        var thumb = Thumbnail ?? Array.Empty<byte>();
        w.Write(thumb.Length);
        w.Write(thumb);

        // Seconda coda facoltativa: le date di modifica, una per voce e nello
        // stesso ordine delle voci.
        //
        // Si scrive SOLO se almeno una e' nota. Cosi' un'offerta senza date resta
        // byte per byte identica a come la scriveva la versione precedente — e i
        // vettori di conformita' lo dimostrano, restando invariati.
        if (Entries.All(e => e.ModifiedUnixMs == 0)) return;

        w.Write(Entries.Count);
        foreach (var e in Entries)
            w.Write(e.ModifiedUnixMs);
    }

    public static FileOffer ReadFrom(BinaryReader r)
    {
        var offer = new FileOffer
        {
            OfferId = new Guid(r.ReadBytes(16)),
            OwnerDeviceId = ReadString(r),
            OwnerName = ReadString(r),
        };
        // Come per le lunghezze: il numero di voci arriva dal mittente. Ogni voce
        // occupa almeno 17 byte sul filo, quindi piu' di cosi' non ce ne stanno.
        var count = r.ReadInt32();
        var room = (r.BaseStream.Length - r.BaseStream.Position) / MinEntryBytes;
        if (count < 0 || count > Math.Min(MaxEntries, room))
            throw new InvalidDataException($"offerta con {count} voci dichiarate, spazio per {room}");

        for (var i = 0; i < count; i++)
        {
            offer.Entries.Add(new FileEntry
            {
                RootIndex = r.ReadInt32(),
                IsDir = r.ReadBoolean(),
                Size = r.ReadInt64(),
                RelativePath = ReadString(r),
            });
        }

        // La miniatura, se c'e'. Un mittente di versione precedente non la manda
        // affatto: l'assenza non e' un errore, e non deve far perdere l'offerta.
        // Se una coda si salta senza consumarla, il flusso resta disallineato e
        // tutto cio' che segue va letto storto: da li' in poi non si legge piu'.
        var aligned = true;
        try
        {
            var len = ClipboardPayload.ReadLength(r);
            if (len > 0 && len <= MaxThumbnailBytes)
                offer.Thumbnail = r.ReadBytes(len);
            else if (len > MaxThumbnailBytes)
                aligned = false;
            // Piu' grande del tetto: si ignora e basta. Non e' un motivo per
            // buttare via un'offerta per il resto valida.
        }
        catch (EndOfStreamException)
        {
        }

        // Le date, se ci sono. Un mittente di versione precedente non le manda:
        // l'assenza non e' un errore, e le voci restano a zero.
        if (aligned)
        {
            try
            {
                // Il numero deve coincidere con le voci gia' lette: se non
                // coincide non sono le nostre date, e non si tocca niente.
                if (r.ReadInt32() == offer.Entries.Count)
                    foreach (var e in offer.Entries)
                        e.ModifiedUnixMs = r.ReadInt64();
            }
            catch (EndOfStreamException)
            {
            }
        }

        return offer;
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(b.Length);
        w.Write(b);
    }

    private static string ReadString(BinaryReader r) =>
        Encoding.UTF8.GetString(r.ReadBytes(ClipboardPayload.ReadLength(r)));

    // ----- Costruzione dai percorsi della clipboard (lato host) -----

    /// <summary>
    /// Costruisce un'offerta camminando il filesystem. Le cartelle vengono
    /// espanse ricorsivamente (file + sottocartelle, comprese quelle vuote).
    /// Ritorna null se supera <see cref="MaxEntries"/>.
    /// </summary>
    public static FileOffer? FromPaths(IEnumerable<string> topLevelPaths, string ownerDeviceId, string ownerName)
    {
        var offer = new FileOffer
        {
            OfferId = Guid.NewGuid(),
            OwnerDeviceId = ownerDeviceId,
            OwnerName = ownerName,
            RootParents = new List<string>(),
        };

        var rootIndex = 0;
        foreach (var raw in topLevelPaths)
        {
            var path = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name))
                continue;

            offer.RootParents.Add(parent);

            if (File.Exists(path))
            {
                offer.Entries.Add(new FileEntry
                {
                    RootIndex = rootIndex,
                    IsDir = false,
                    Size = SafeSize(path),
                    ModifiedUnixMs = SafeModified(path),
                    RelativePath = name,
                });
            }
            else if (Directory.Exists(path))
            {
                if (!AddDirectory(offer, rootIndex, parent, path))
                    return null; // troppo grande
            }
            else
            {
                offer.RootParents.RemoveAt(offer.RootParents.Count - 1);
                continue;
            }

            rootIndex++;
        }

        return offer.Entries.Count > 0 ? offer : null;
    }

    private static bool AddDirectory(FileOffer offer, int rootIndex, string parent, string dirPath)
    {
        // La radice stessa e le sottocartelle (per preservare le cartelle vuote).
        offer.Entries.Add(new FileEntry
        {
            RootIndex = rootIndex,
            IsDir = true,
            RelativePath = Rel(parent, dirPath),
        });

        IEnumerable<string> subDirs, files;
        try
        {
            subDirs = Directory.EnumerateDirectories(dirPath, "*", SearchOption.AllDirectories);
            files = Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return true; // accesso negato: ci fermiamo a quello che abbiamo
        }

        foreach (var d in subDirs)
        {
            offer.Entries.Add(new FileEntry { RootIndex = rootIndex, IsDir = true, RelativePath = Rel(parent, d) });
            if (offer.Entries.Count > MaxEntries)
                return false;
        }
        foreach (var f in files)
        {
            offer.Entries.Add(new FileEntry
            {
                RootIndex = rootIndex,
                IsDir = false,
                Size = SafeSize(f),
                ModifiedUnixMs = SafeModified(f),
                RelativePath = Rel(parent, f),
            });
            if (offer.Entries.Count > MaxEntries)
                return false;
        }
        return true;
    }

    private static string Rel(string parent, string full) =>
        Path.GetRelativePath(parent, full).Replace('\\', '/');

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>Ultima modifica in millisecondi Unix, o 0 se non si e' potuta leggere.</summary>
    private static long SafeModified(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(); }
        catch { return 0; }
    }
}
