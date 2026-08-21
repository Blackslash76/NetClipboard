using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace NetClipboard.Droid.Views;

/// <summary>
/// Il poco che serve per scrivere testo dentro a un controllo disegnato a mano:
/// comporre una riga e appoggiarla in un rettangolo.
///
/// Su Windows lo stesso lavoro lo fa <c>TextRenderer.DrawText</c> con i suoi
/// flag; qui non c'e' un equivalente pronto, e senza queste due funzioni ogni
/// riga di ogni elenco si porterebbe dietro le stesse dieci righe di
/// impaginazione copiate.
/// </summary>
public static class Ink
{
    /// <summary>
    /// Prepara una riga di testo. Se si dichiara una larghezza massima, il testo
    /// che non ci sta finisce con i puntini invece di andare a capo: in un elenco
    /// l'altezza della riga e' decisa, e un testo che va a capo esce dalla scheda.
    /// </summary>
    public static FormattedText Lay(string text, double size, FontWeight weight, Color color,
                                    double maxWidth = double.PositiveInfinity)
    {
        var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), size, Palette.Brush(color));
        if (!double.IsInfinity(maxWidth))
        {
            t.MaxTextWidth = Math.Max(1, maxWidth);
            t.Trimming = TextTrimming.CharacterEllipsis;
        }
        t.MaxLineCount = 1;
        return t;
    }

    /// <summary>Scrive dentro al rettangolo, centrato in verticale e — se richiesto — anche in orizzontale.</summary>
    public static void Draw(DrawingContext ctx, string text, double size, FontWeight weight,
                            Color color, Rect box, bool center = false)
    {
        var laid = Lay(text, size, weight, color, center ? double.PositiveInfinity : box.Width);
        var x = center ? box.X + (box.Width - laid.Width) / 2 : box.X;
        var y = box.Y + (box.Height - laid.Height) / 2;
        ctx.DrawText(laid, new Point(x, y));
    }
}
