using System.Globalization;
using System.Text;

namespace NetClipboard.Core;

/// <summary>
/// Il formato CF_HTML di Windows: un frammento di HTML preceduto da un'intestazione
/// che dice dove comincia e dove finisce.
///
/// L'insidia e' che quegli scarti sono <b>in byte</b> sulla codifica UTF-8, non in
/// caratteri: una sola lettera accentata prima del frammento basta a spostare tutto
/// di uno, e chi incolla si ritrova mezzo tag. Per questo qui si misura sempre su
/// <see cref="Encoding.UTF8"/> e mai su <c>string.Length</c>.
///
/// In rete viaggia solo il <b>frammento</b>, non il CF_HTML intero: l'intestazione
/// si ricostruisce dall'altra parte. Cosi' gli scarti sono per forza giusti, e non
/// si porta in giro il campo SourceURL, che spesso e' un percorso locale di chi ha
/// copiato.
/// </summary>
public static class CfHtml
{
    private const string FragStart = "<!--StartFragment-->";
    private const string FragEnd = "<!--EndFragment-->";
    private const string Head = "<html><body>\r\n";
    private const string Tail = "\r\n</body></html>";

    /// <summary>Intestazione con i quattro scarti; le cifre sono sempre dieci, cosi' la sua lunghezza non cambia.</summary>
    private static string Header(int startHtml, int endHtml, int startFragment, int endFragment) =>
        string.Format(CultureInfo.InvariantCulture,
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n",
            startHtml, endHtml, startFragment, endFragment);

    /// <summary>Confeziona un frammento come CF_HTML valido, pronto per la clipboard.</summary>
    public static string Build(string fragment)
    {
        // Due passate: la prima serve solo a sapere quanto e' lunga l'intestazione,
        // che essendo a cifre fisse non cambiera' nella seconda.
        var headerLen = Encoding.UTF8.GetByteCount(Header(0, 0, 0, 0));
        var startHtml = headerLen;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(Head + FragStart);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(FragEnd + Tail);
        return Header(startHtml, endHtml, startFragment, endFragment)
             + Head + FragStart + fragment + FragEnd + Tail;
    }

    /// <summary>
    /// Estrae il frammento da un CF_HTML letto dalla clipboard. Null se non c'e'
    /// niente di utile.
    ///
    /// Si prova prima con gli scarti dichiarati, che sono la verita' del formato;
    /// se sono incoerenti — capita, e non e' colpa di chi incolla — si ripiega sui
    /// commenti e infine su tutto cio' che segue l'intestazione.
    /// </summary>
    public static string? ExtractFragment(string? cfHtml)
    {
        if (string.IsNullOrEmpty(cfHtml)) return null;
        var bytes = Encoding.UTF8.GetBytes(cfHtml);

        var start = HeaderValue(cfHtml, "StartFragment");
        var end = HeaderValue(cfHtml, "EndFragment");
        if (start >= 0 && end > start && end <= bytes.Length)
            return Encoding.UTF8.GetString(bytes, start, end - start).Trim();

        var i = cfHtml.IndexOf(FragStart, StringComparison.OrdinalIgnoreCase);
        var j = cfHtml.LastIndexOf(FragEnd, StringComparison.OrdinalIgnoreCase);
        if (i >= 0 && j > i)
            return cfHtml[(i + FragStart.Length)..j].Trim();

        var h = cfHtml.IndexOf('<');
        var body = h >= 0 ? cfHtml[h..].Trim() : cfHtml.Trim();
        return body.Length > 0 ? body : null;
    }

    /// <summary>Valore intero di una riga dell'intestazione, -1 se assente o illeggibile.</summary>
    private static int HeaderValue(string cfHtml, string key)
    {
        var at = cfHtml.IndexOf(key + ":", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        var from = at + key.Length + 1;
        var to = from;
        while (to < cfHtml.Length && char.IsAsciiDigit(cfHtml[to])) to++;
        return to > from && int.TryParse(cfHtml[from..to], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v : -1;
    }
}
