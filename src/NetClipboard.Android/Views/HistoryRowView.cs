using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NetClipboard.Core;

namespace NetClipboard.Droid.Views;

/// <summary>
/// Una riga della cronologia, disegnata a mano.
///
/// E' la traduzione uno-a-uno di <c>HistoryForm.DrawRow</c> dell'applicazione
/// Windows: stessa scheda arrotondata, stesso distintivo con il gradiente per
/// tipo di contenuto, stesso anello di scadenza attorno all'avatar, stessa riga
/// di dettagli sotto l'anteprima. Le misure sono piu' generose perche' qui il
/// puntatore e' un dito: la riga arriva a 68 e l'avatar a 44, sopra i 48 che
/// Android considera il minimo per un bersaglio da toccare.
///
/// Disegnata invece che composta con controlli: l'anello di scadenza va
/// ridisegnato ogni secondo, e con dei controlli veri significherebbe rifare il
/// layout dell'intera lista a ogni battito.
/// </summary>
public sealed class HistoryRowView : Control
{
    public const double RowHeight = 68;
    private const double AvatarSize = 44;

    private readonly ClipboardHistory _history;
    private Bitmap? _thumb;
    private bool _thumbTried;
    private bool _pressed;

    /// <summary>La voce mostrata. Si tocca per portarla negli appunti.</summary>
    public HistoryItem Item { get; }

    /// <summary>Tocco breve: e' il gesto deliberato che sostituisce la sovrascrittura automatica.</summary>
    public event Action<HistoryItem>? Chosen;

    /// <summary>Tocco lungo: apre le azioni sulla voce (pin, elimina).</summary>
    public event Action<HistoryItem>? OptionsRequested;

    public HistoryRowView(HistoryItem item, ClipboardHistory history)
    {
        Item = item;
        _history = history;
        Height = RowHeight;
        // Il tocco lungo apre le azioni sulla voce: e' una proprieta' allegata,
        // senza scorciatoia sull'istanza.
        SetValue(InputElement.IsHoldingEnabledProperty, true);

        Tapped += (_, _) => Chosen?.Invoke(Item);
        Holding += (_, e) =>
        {
            if (e.HoldingState != HoldingState.Started) return;
            OptionsRequested?.Invoke(Item);
        };

        PointerPressed += (_, _) => SetPressed(true);
        PointerReleased += (_, _) => SetPressed(false);
        PointerCaptureLost += (_, _) => SetPressed(false);
        PointerExited += (_, _) => SetPressed(false);
    }

    private void SetPressed(bool value)
    {
        if (_pressed == value) return;
        _pressed = value;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(availableSize.Width, RowHeight);

    public override void Render(DrawingContext ctx)
    {
        var row = new Rect(6, 3, Math.Max(0, Bounds.Width - 12), RowHeight - 6);
        var spent = ClipboardHistory.IsSpent(Item);

        ctx.DrawRectangle(Palette.Brush(_pressed ? Palette.Hover : Palette.Card), null,
            new RoundedRect(row, 10));

        // Il segno di accento a sinistra: su Windows marca la riga selezionata,
        // qui — dove non esiste una selezione — marca quella con il pin.
        if (Item.Pinned)
            ctx.DrawRectangle(Palette.Brush(Palette.Accent), null,
                new RoundedRect(new Rect(row.X, row.Y + 8, 3, row.Height - 16), 2));

        var slot = new Rect(row.X + 12, row.Y + (row.Height - AvatarSize) / 2, AvatarSize, AvatarSize);

        // Sugli esterni l'avatar si stringe per lasciare posto all'anello di
        // scadenza che lo circonda: l'informazione sta addosso al contenuto.
        var icon = Item.FromExternal ? slot.Deflate(5) : slot;

        var thumb = Thumbnail();
        if (thumb != null)
        {
            using (ctx.PushClip(new RoundedRect(icon, 9)))
                ctx.DrawImage(thumb, new Rect(thumb.Size), icon);
        }
        else
        {
            var (a, b, glyph) = BadgeStyle(Item);
            ctx.DrawRectangle(Palette.Diagonal(a, b), null, new RoundedRect(icon, 9));
            Ink.Draw(ctx, glyph, 17, FontWeight.Bold, Palette.OnAccent, icon, center: true);
        }

        if (Item.FromExternal && !spent)
            DrawExpiryRing(ctx, slot, ClipboardHistory.RemainingFraction(Item));

        // L'avatar di una riga spenta si smorza, cosi' la riga si legge come
        // disattivata gia' dalla coda dell'occhio, prima di leggere l'etichetta.
        if (spent)
            ctx.FillRectangle(Palette.Brush(Palette.Card, 150), slot);

        var textLeft = slot.Right + 12;
        var textWidth = row.Right - textLeft - 12;

        if (spent)
        {
            var tag = L.T(Item.Used ? "history.used" : "history.expired");
            var laid = Ink.Lay(tag, 12, FontWeight.Normal, Palette.TextSpent);
            ctx.DrawText(laid, new Point(row.Right - 12 - laid.Width, row.Y + 10));
            textWidth -= laid.Width + 10;
        }

        if (textWidth <= 0) return;

        Ink.Draw(ctx, OneLine(Item.Preview), 15, FontWeight.Normal,
            spent ? Palette.TextSpent : Palette.TextMain,
            new Rect(textLeft, row.Y + 9, textWidth, 20));

        Ink.Draw(ctx, MetaLine(), 12, FontWeight.Normal,
            spent ? Palette.TextSpent : Palette.TextMuted,
            new Rect(textLeft, row.Y + 33, textWidth, 18));
    }

    private string MetaLine()
    {
        var pin = Item.Pinned ? "📌 " : "";
        var toFetch = Item.Kind == PayloadKind.Files && !Item.IsLocalOffer
            && (Item.LocalRootPaths == null || Item.LocalRootPaths.Count == 0) ? L.T("history.toDownload") : "";
        var origin = Item.IsLocal ? L.T("history.thisDevice") : Item.Origin;
        if (Item.FromExternal) origin = L.T("history.external", origin);
        return L.T("history.meta", pin, origin, TimeText.Relative(Item.TimestampUtc), toFetch);
    }

    /// <summary>
    /// L'anteprima di un contenuto e' una riga sola: se dentro ci sono a capo,
    /// qui diventerebbero altezza sprecata e testo tagliato a meta'.
    /// </summary>
    private static string OneLine(string s) =>
        s.Replace('\r', ' ').Replace('\n', ' ');

    private static (Color, Color, string) BadgeStyle(HistoryItem item) => item.Kind switch
    {
        PayloadKind.Text => (Palette.TextKindA, Palette.TextKindB, "T"),
        PayloadKind.Files => (Palette.FileWarmA, Palette.FileWarmB, item.DirCount > 0 ? "🗀" : "🗎"),
        PayloadKind.Image => (Palette.OtherKindA, Palette.OtherKindB, "🖼"),
        _ => (Palette.OtherKindA, Palette.OtherKindB, "?"),
    };

    /// <summary>
    /// Anello di scadenza attorno all'avatar: si consuma in senso orario col tempo
    /// residuo e vira all'arancione sull'ultimo quarto, quando conviene sbrigarsi.
    /// </summary>
    private static void DrawExpiryRing(DrawingContext ctx, Rect box, double fraction)
    {
        var color = fraction <= 0.25 ? Palette.Warn : Palette.Info;
        var thickness = Math.Max(2.0, box.Width / 14);
        var arc = box.Deflate(thickness / 2);
        var radius = arc.Width / 2;
        var center = arc.Center;

        ctx.DrawEllipse(null, new Pen(Palette.Brush(Palette.TextMuted, 60), thickness), center, radius, radius);
        if (fraction <= 0) return;

        // Un cerchio intero non si disegna con un arco: l'inizio e la fine
        // coincidono e il tracciato resta vuoto. Sopra il 99.9% si disegna
        // l'ellisse, che e' anche cio' che si vede il primo secondo di vita.
        var pen = new Pen(Palette.Brush(color), thickness) { LineCap = PenLineCap.Round };
        if (fraction >= 0.999)
        {
            ctx.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var sweep = 360.0 * fraction;
        var end = new Point(
            center.X + radius * Math.Sin(sweep * Math.PI / 180),
            center.Y - radius * Math.Cos(sweep * Math.PI / 180));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X, center.Y - radius), false);
            g.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geometry);
    }

    // ----- miniatura -----

    /// <summary>
    /// La miniatura, se c'e' ed e' lecito decodificarla.
    ///
    /// La regola e' la stessa di Windows, e vale la pena ripeterla: la miniatura
    /// di un'offerta la fornisce CHI MANDA. Da un dispositivo fidato va bene; da
    /// un estraneo che ci ha mandato qualcosa no — quello e' esattamente il
    /// momento in cui non ci si fida, e un decodificatore di immagini e' una
    /// superficie d'attacco. Li' resta il distintivo.
    /// </summary>
    private Bitmap? Thumbnail()
    {
        if (_thumbTried) return _thumb;
        _thumbTried = true;

        if (Item.BlobFile == null) return null;
        if (Item.Kind != PayloadKind.Image && Item.Kind != PayloadKind.Files) return null;
        if (Item.Kind == PayloadKind.Files && Item.FromExternal) return null;

        try
        {
            var png = _history.ReadBlob(Item);
            if (png == null) return null;
            using var ms = new MemoryStream(png);
            _thumb = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            Log.Write($"[UI] miniatura illeggibile: {ex.Message}");
        }
        return _thumb;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _thumb?.Dispose();
        _thumb = null;
        _thumbTried = false;
        base.OnDetachedFromVisualTree(e);
    }
}
