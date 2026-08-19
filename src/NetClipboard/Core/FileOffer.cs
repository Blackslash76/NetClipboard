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
}
