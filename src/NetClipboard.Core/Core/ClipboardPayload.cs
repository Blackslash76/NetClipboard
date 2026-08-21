using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NetClipboard.Core;

public enum PayloadKind : byte
{
    Text = 1,
    Image = 2,
    Files = 3,
}

/// <summary>
/// Contenuto neutro della clipboard, pronto per la rete. Testo e immagini
/// viaggiano per valore; i file viaggiano come <see cref="FileOffer"/> (solo
/// metadati: i byte vengono richiesti su incolla).
///
/// Serializzazione binaria:
///   [kind:1]
///   Text  -> [len:4][utf8]  poi, facoltativi, [tag:1][len:4][utf8]...
///   Image -> [len:4][png bytes]
///   Files -> FileOffer.WriteTo
///
/// Le code del testo (HTML, RTF) sono aggiunte <b>in fondo</b> di proposito, e non
/// con un <see cref="PayloadKind"/> nuovo: un peer di versione precedente si ferma
/// dopo il testo senza guardare se il buffer e' finito, quindi le ignora e riceve
/// comunque il contenuto. Un tipo sconosciuto, invece, gli farebbe sollevare
/// "Tipo payload sconosciuto" e chiudere la connessione.
/// </summary>
public sealed class ClipboardPayload
{
    /// <summary>Etichette delle code facoltative del testo. Una sconosciuta si salta, non fa errore.</summary>
    private const byte TagHtml = 1;
    private const byte TagRtf = 2;

    /// <summary>
    /// Tetto per i formati ricchi. L'HTML che Word mette in clipboard puo' pesare
    /// megabyte per un paragrafo: oltre questa soglia si degrada al testo semplice,
    /// che e' cio' che serviva, invece di gonfiare ogni invio.
    /// </summary>
    public const int MaxRichBytes = 2 * 1024 * 1024;

    public PayloadKind Kind { get; init; }
    public string? Text { get; init; }

    /// <summary>Frammento HTML (senza intestazione CF_HTML), se il contenuto ne aveva uno.</summary>
    public string? Html { get; init; }

    /// <summary>Testo RTF, se il contenuto ne aveva uno.</summary>
    public string? Rtf { get; init; }

    public byte[]? ImagePng { get; init; }
    public FileOffer? Offer { get; init; }

    /// <summary>True se c'e' formattazione da preservare oltre al testo nudo.</summary>
    public bool HasRichText => Html != null || Rtf != null;

    public static ClipboardPayload FromText(string text) =>
        new() { Kind = PayloadKind.Text, Text = text };

    /// <summary>
    /// Testo con la sua formattazione. Le code oltre <see cref="MaxRichBytes"/> si
    /// lasciano cadere qui, in un punto solo, cosi' il tetto vale per chiunque
    /// costruisca un payload.
    /// </summary>
    public static ClipboardPayload FromRichText(string text, string? html, string? rtf) =>
        new()
        {
            Kind = PayloadKind.Text,
            Text = text,
            Html = WithinCap(html),
            Rtf = WithinCap(rtf),
        };

    private static string? WithinCap(string? s) =>
        string.IsNullOrEmpty(s) || Encoding.UTF8.GetByteCount(s) > MaxRichBytes ? null : s;

    public static ClipboardPayload FromImage(byte[] png) =>
        new() { Kind = PayloadKind.Image, ImagePng = png };

    public static ClipboardPayload FromOffer(FileOffer offer) =>
        new() { Kind = PayloadKind.Files, Offer = offer };

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)Kind);
        switch (Kind)
        {
            case PayloadKind.Text:
                var tb = Encoding.UTF8.GetBytes(Text ?? "");
                w.Write(tb.Length);
                w.Write(tb);
                WriteTail(w, TagHtml, Html);
                WriteTail(w, TagRtf, Rtf);
                break;
            case PayloadKind.Image:
                var img = ImagePng ?? Array.Empty<byte>();
                w.Write(img.Length);
                w.Write(img);
                break;
            case PayloadKind.Files:
                (Offer ?? new FileOffer()).WriteTo(w);
                break;
        }
        w.Flush();
        return ms.ToArray();
    }

    public static ClipboardPayload Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        var kind = (PayloadKind)r.ReadByte();
        switch (kind)
        {
            case PayloadKind.Text:
                return ReadText(r);
            case PayloadKind.Image:
                return FromImage(r.ReadBytes(ReadLength(r)));
            case PayloadKind.Files:
                return FromOffer(FileOffer.ReadFrom(r));
            default:
                throw new InvalidDataException($"Tipo payload sconosciuto: {(byte)kind}");
        }
    }

    private static void WriteTail(BinaryWriter w, byte tag, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write(tag);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    /// <summary>
    /// Testo piu' le code che ci fossero. Si legge finche' ci sono byte: chi manda
    /// non dichiara quante code ha messo, e non deve — chi non le capisce si ferma
    /// prima e chi le capisce le trova qui.
    /// </summary>
    private static ClipboardPayload ReadText(BinaryReader r)
    {
        var text = Encoding.UTF8.GetString(r.ReadBytes(ReadLength(r)));
        string? html = null, rtf = null;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var tag = r.ReadByte();
            // ReadLength serve anche qui: la coda arriva dalla rete come tutto il
            // resto, e una lunghezza inventata alloca prima di fallire.
            var value = Encoding.UTF8.GetString(r.ReadBytes(ReadLength(r)));
            switch (tag)
            {
                case TagHtml: html = value; break;
                case TagRtf: rtf = value; break;
                // Coda di una versione futura: si salta e si va avanti. Ignorarla
                // e' esattamente cio' che ci si aspetta da noi.
            }
        }
        return FromRichText(text, html, rtf);
    }

    /// <summary>
    /// Lunghezza dichiarata dal mittente, accettata solo se quei byte ci sono davvero.
    ///
    /// Serve perche' <see cref="BinaryReader.ReadBytes"/> alloca subito l'intero
    /// buffer richiesto e NON solleva niente se il flusso finisce prima: senza
    /// questo controllo un messaggio di nove byte che dichiarava 600 MB li faceva
    /// allocare davvero, e per giunta senza errore. I dati arrivano dalla rete,
    /// anche da chi non e' accoppiato: la lunghezza va sempre confrontata con lo
    /// spazio reale.
    /// </summary>
    internal static int ReadLength(BinaryReader r)
    {
        var len = r.ReadInt32();
        var left = r.BaseStream.Length - r.BaseStream.Position;
        if (len < 0 || len > left)
            throw new InvalidDataException($"lunghezza dichiarata {len}, disponibili {left}");
        return len;
    }

    /// <summary>
    /// Hash stabile del contenuto, per de-duplica e anti-loop. Per i file usa
    /// owner + elenco relativo (NON l'OfferId), cosi ricopiare gli stessi file
    /// non crea doppioni in cronologia.
    /// </summary>
    public string ContentHash()
    {
        byte[] bytes;
        if (Kind == PayloadKind.Files && Offer != null)
        {
            var sb = new StringBuilder();
            sb.Append(Offer.OwnerDeviceId);
            foreach (var e in Offer.Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
            {
                sb.Append('|').Append(e.RelativePath).Append(':').Append(e.Size);

                // La data entra solo se e' nota. Due ragioni: un'offerta senza
                // date deve continuare a produrre l'impronta di sempre (i vettori
                // di conformita' lo verificano), e una data mancante non deve
                // diventare uno zero che finge di essere un'informazione.
                if (e.ModifiedUnixMs != 0) sb.Append(':').Append(e.ModifiedUnixMs);
            }
            bytes = Encoding.UTF8.GetBytes(sb.ToString());
        }
        else if (Kind == PayloadKind.Text)
        {
            // Solo il testo, non la formattazione. Due motivi: ricopiare lo stesso
            // paragrafo da due programmi diversi e' la stessa cosa per chi guarda
            // l'elenco; e la soppressione dell'eco confronta cio' che rimettiamo in
            // clipboard con cio' che rileggiamo, e l'HTML non torna indietro
            // identico al byte — bastava quello per riaprire il ping-pong.
            bytes = FromText(Text ?? "").Serialize();
        }
        else
        {
            bytes = Serialize();
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public long ApproxSize()
    {
        return Kind switch
        {
            PayloadKind.Text => ((Text?.Length ?? 0) + (Html?.Length ?? 0) + (Rtf?.Length ?? 0)) * 2L,
            PayloadKind.Image => ImagePng?.Length ?? 0,
            PayloadKind.Files => Offer?.TotalSize ?? 0,
            _ => 0,
        };
    }

    public string ShortPreview()
    {
        switch (Kind)
        {
            case PayloadKind.Text:
                var t = (Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                return t.Length > 80 ? t[..80] + "…" : t;
            case PayloadKind.Image:
                return L.T("preview.image", (ImagePng?.Length ?? 0) / 1024);
            case PayloadKind.Files:
                return Offer != null ? FileSummary(Offer) : L.T("preview.files");
            default:
                return L.T("preview.unknown");
        }
    }

    public static string FileSummary(FileOffer offer)
    {
        var names = string.Join(", ", offer.TopLevelNames.Take(3));
        var more = offer.TopLevelNames.Count() > 3 ? "…" : "";
        var what = offer.DirCount > 0
            ? L.T("preview.filesAndDirs", offer.FileCount, offer.DirCount)
            : L.T("preview.filesOnly", offer.FileCount);
        return L.T("preview.fileSummary", what, HumanSize(offer.TotalSize), names, more);
    }

    public static string HumanSize(long bytes)
    {
        string[] units = { "unit.b", "unit.kb", "unit.mb", "unit.gb", "unit.tb" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return L.T("unit.format", v, L.T(units[i]));
    }
}
