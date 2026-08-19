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
///   Text  -> [len:4][utf8]
///   Image -> [len:4][png bytes]
///   Files -> FileOffer.WriteTo
/// </summary>
public sealed class ClipboardPayload
{
    public PayloadKind Kind { get; init; }
    public string? Text { get; init; }
    public byte[]? ImagePng { get; init; }
    public FileOffer? Offer { get; init; }

    public static ClipboardPayload FromText(string text) =>
        new() { Kind = PayloadKind.Text, Text = text };

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
                return FromText(Encoding.UTF8.GetString(r.ReadBytes(ReadLength(r))));
            case PayloadKind.Image:
                return FromImage(r.ReadBytes(ReadLength(r)));
            case PayloadKind.Files:
                return FromOffer(FileOffer.ReadFrom(r));
            default:
                throw new InvalidDataException($"Tipo payload sconosciuto: {(byte)kind}");
        }
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
                sb.Append('|').Append(e.RelativePath).Append(':').Append(e.Size);
            bytes = Encoding.UTF8.GetBytes(sb.ToString());
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
            PayloadKind.Text => (Text?.Length ?? 0) * 2L,
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
