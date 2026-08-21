using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace NetClipboard.Droid.Views;

/// <summary>
/// Il pulsante dell'applicazione: piatto, arrotondato, coi colori della
/// tavolozza.
///
/// Non e' il <c>Button</c> di Avalonia perche' quello porta con se' l'aspetto di
/// Fluent — angoli, bordi e tinte suoi — e accanto a righe disegnate da noi con
/// la tavolozza di Windows si vedrebbe che vengono da due posti diversi. E' la
/// stessa scelta fatta sul PC, dove i pulsanti sono <c>FlatStyle.Flat</c> con i
/// colori del tema invece di quelli di sistema.
/// </summary>
public sealed class PillButton : Control
{
    private bool _pressed;
    private string _text = "";

    /// <summary>Pieno di colore (azione principale) oppure appena accennato (azione secondaria).</summary>
    public bool Filled { get; set; }

    /// <summary>Tinta del pulsante: il pieno la usa come fondo, l'altro come testo.</summary>
    public Func<Color> Tint { get; set; } = () => Palette.Accent;

    public event Action? Click;

    public PillButton(string text, bool filled = false)
    {
        _text = text;
        Filled = filled;
        Height = 46;
        Tapped += (_, _) => { if (IsEffectivelyEnabled) Click?.Invoke(); };
        PointerPressed += (_, _) => SetPressed(true);
        PointerReleased += (_, _) => SetPressed(false);
        PointerCaptureLost += (_, _) => SetPressed(false);
        PointerExited += (_, _) => SetPressed(false);
    }

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; InvalidateVisual(); }
    }

    private void SetPressed(bool value)
    {
        if (_pressed == value) return;
        _pressed = value;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var box = new RoundedRect(new Rect(Bounds.Size), 12);
        var tint = Tint();
        var on = IsEffectivelyEnabled;

        // Premuto si scurisce di un velo invece di cambiare colore: cosi' vale sia
        // sul pieno che sul vuoto, senza una seconda tinta da scegliere.
        if (Filled)
        {
            ctx.DrawRectangle(Palette.Brush(tint, on ? (byte)255 : (byte)90), null, box);
            if (_pressed) ctx.DrawRectangle(Palette.Brush(Colors.Black, 40), null, box);
            Ink.Draw(ctx, Text, 15, FontWeight.SemiBold,
                on ? Palette.OnAccent : Color.FromArgb(160, 255, 255, 255), new Rect(Bounds.Size), center: true);
        }
        else
        {
            ctx.DrawRectangle(Palette.Brush(_pressed ? Palette.Hover : Palette.ButtonFace), null, box);
            ctx.DrawRectangle(null, new Pen(Palette.Brush(Palette.Divider), 1), box);
            Ink.Draw(ctx, Text, 15, FontWeight.SemiBold,
                on ? Palette.ButtonText : Palette.TextSpent, new Rect(Bounds.Size), center: true);
        }
    }
}
