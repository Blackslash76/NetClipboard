using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace NetClipboard.Droid.Views;

/// <summary>
/// La stessa tavolozza dell'applicazione Windows, portata su Avalonia.
///
/// I valori sono copiati uno per uno da <c>Ui/Theme.cs</c> e vanno tenuti
/// allineati: e' l'unica cosa che fa sembrare le due applicazioni la stessa
/// applicazione. Non stanno nel core perche' li' non ci sono tipi di disegno —
/// il core non conosce ne' System.Drawing ne' Avalonia, ed e' bene che resti
/// cosi'.
///
/// Chiaro o scuro lo decide il sistema, come su Windows: qui arriva da
/// <c>ActualThemeVariant</c>, e chi disegna si riaggancia a <see cref="Changed"/>.
/// </summary>
public static class Palette
{
    /// <summary>Vero se si sta disegnando in tema scuro.</summary>
    public static bool Dark { get; private set; }

    /// <summary>Il tema e' cambiato: chi disegna si ridipinga.</summary>
    public static event Action? Changed;

    /// <summary>Da chiamare quando il sistema cambia modalita'. Se non cambia niente, non fa niente.</summary>
    public static void Use(bool dark)
    {
        if (dark == Dark) return;
        Dark = dark;
        _brushes.Clear();
        Changed?.Invoke();
    }

    private static Color Pick(Color dark, Color light) => Dark ? dark : light;

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    // ----- gli stessi colori, con gli stessi nomi -----

    /// <summary>Fondo della schermata.</summary>
    public static Color Bg => Pick(Rgb(26, 26, 32), Rgb(246, 246, 250));

    /// <summary>Fondo di una scheda/riga appoggiata sul fondo della schermata.</summary>
    public static Color Card => Pick(Rgb(38, 38, 47), Rgb(255, 255, 255));

    /// <summary>Fascia dell'intestazione.</summary>
    public static Color HeaderBg => Pick(Rgb(33, 33, 41), Rgb(238, 238, 244));

    /// <summary>Riga premuta (sul telefono non esiste il puntatore: e' il feedback al tocco).</summary>
    public static Color Hover => Pick(Rgb(48, 48, 60), Rgb(234, 234, 242));

    /// <summary>Riga selezionata.</summary>
    public static Color Sel => Pick(Rgb(52, 50, 74), Rgb(232, 228, 252));

    public static Color TextMain => Pick(Rgb(238, 238, 244), Rgb(26, 26, 32));
    public static Color TextMuted => Pick(Rgb(150, 152, 165), Rgb(102, 104, 118));

    /// <summary>Testo di cio' che e' passato: usato, scaduto, non piu' utilizzabile.</summary>
    public static Color TextSpent => Pick(Rgb(104, 106, 118), Rgb(158, 160, 172));

    public static Color Accent => Pick(Rgb(120, 92, 245), Rgb(102, 72, 232));
    public static Color AccentAlt => Pick(Rgb(56, 180, 220), Rgb(28, 152, 198));

    /// <summary>Testo sopra una superficie di accento (sempre chiaro, in entrambi i temi).</summary>
    public static Color OnAccent => Colors.White;

    public static Color Divider => Pick(Rgb(48, 48, 59), Rgb(222, 222, 230));

    public static Color ButtonFace => Pick(Rgb(58, 58, 70), Rgb(232, 232, 238));
    public static Color ButtonText => Pick(Rgb(240, 240, 246), Rgb(30, 30, 38));

    /// <summary>Fondo dei campi di immissione e delle liste.</summary>
    public static Color Field => Pick(Rgb(40, 40, 49), Rgb(255, 255, 255));

    public static Color Success => Pick(Rgb(90, 210, 130), Rgb(24, 140, 78));
    public static Color Info => Pick(Rgb(120, 200, 255), Rgb(20, 108, 186));
    public static Color Warn => Pick(Rgb(240, 170, 70), Rgb(186, 112, 16));

    /// <summary>Pulsante che porta avanti l'azione principale.</summary>
    public static Color Primary => Pick(Rgb(30, 120, 200), Rgb(24, 104, 180));

    /// <summary>Estremi del gradiente caldo usato per i contenuti "file".</summary>
    public static Color FileWarmA => Pick(Rgb(244, 176, 66), Rgb(250, 178, 62));
    public static Color FileWarmB => Pick(Rgb(222, 132, 40), Rgb(232, 138, 36));

    /// <summary>Estremi del gradiente freddo usato per i contenuti "testo".</summary>
    public static Color TextKindA => Pick(Rgb(70, 120, 235), Rgb(78, 126, 240));
    public static Color TextKindB => Pick(Rgb(60, 90, 200), Rgb(58, 92, 208));

    /// <summary>Estremi del gradiente usato per i contenuti di tipo ignoto.</summary>
    public static Color OtherKindA => Pick(Rgb(60, 170, 120), Rgb(52, 168, 116));
    public static Color OtherKindB => Pick(Rgb(40, 140, 100), Rgb(34, 138, 96));

    /// <summary>Luminosita' delle tinte derivate da un identificativo (avatar dei dispositivi).</summary>
    public static double AvatarLightness => Dark ? 0.48 : 0.42;

    // ----- pennelli -----

    private static readonly Dictionary<uint, IBrush> _brushes = new();

    /// <summary>
    /// Pennello pieno per un colore, riusato.
    ///
    /// Il disegno di una riga passa di qui una decina di volte, e le righe si
    /// ridisegnano a ogni tocco e a ogni giro dell'anello di scadenza: allocare
    /// un pennello nuovo ogni volta darebbe al raccoglitore un lavoro inutile
    /// proprio mentre si sta scorrendo.
    /// </summary>
    public static IBrush Brush(Color c)
    {
        if (_brushes.TryGetValue(c.ToUInt32(), out var b)) return b;
        b = new ImmutableSolidColorBrush(c);
        _brushes[c.ToUInt32()] = b;
        return b;
    }

    /// <summary>Pennello pieno con trasparenza applicata al colore dato.</summary>
    public static IBrush Brush(Color c, byte alpha) => Brush(Color.FromArgb(alpha, c.R, c.G, c.B));

    /// <summary>Gradiente in diagonale fra due colori: e' quello dei distintivi e del marchio.</summary>
    public static IBrush Diagonal(Color a, Color b) => new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, a), new ImmutableGradientStop(1, b) },
        1, null, null, GradientSpreadMethod.Pad,
        new RelativePoint(0, 0, RelativeUnit.Relative),
        new RelativePoint(1, 1, RelativeUnit.Relative));

    // ----- avatar dei dispositivi -----

    /// <summary>
    /// Tinta derivata dall'identita': stabile fra sessioni e fra dispositivi, ed
    /// e' la stessa formula di <c>RecipientDialog.TintFor</c> su Windows. Lo
    /// stesso PC ha lo stesso colore sul telefono: e' cio' che permette di
    /// riconoscerlo senza leggere il nome.
    /// </summary>
    public static Color TintFor(string? deviceId)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId ?? ""));
        return FromHsl(h[0] / 255.0 * 360.0, 0.45, AvatarLightness);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Iniziali da mostrare nell'avatar quando non c'e' altro da mostrare.</summary>
    public static string Initials(string label)
    {
        var parts = label.Split(new[] { ' ', '·', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
    }
}
