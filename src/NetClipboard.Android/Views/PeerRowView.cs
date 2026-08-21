using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;

namespace NetClipboard.Droid.Views;

/// <summary>
/// Una riga dell'elenco dei dispositivi, con lo stesso taglio delle righe della
/// cronologia e lo stesso avatar del dialogo "Invia a…" di Windows: il colore
/// nasce dall'identita' del dispositivo, quindi lo stesso PC ha lo stesso
/// colore su tutti gli schermi.
///
/// A destra c'e' cio' che si puo' fare: se il dispositivo e' gia' accoppiato,
/// un'etichetta che lo dice; se non lo e', il tocco avvia l'accoppiamento e la
/// riga lo annuncia con una pillola. Nessuna selezione da fare prima: su un
/// telefono "scegli dall'elenco e poi premi il pulsante" e' un passaggio in piu'
/// senza motivo.
/// </summary>
public sealed class PeerRowView : Control
{
    public const double RowHeight = 68;
    private const double AvatarSize = 44;

    private bool _pressed;
    private bool _busy;

    public Peer Peer { get; }

    /// <summary>Tocco su un dispositivo non ancora accoppiato.</summary>
    public event Action<Peer>? PairRequested;

    public PeerRowView(Peer peer)
    {
        Peer = peer;
        Height = RowHeight;

        Tapped += (_, _) =>
        {
            if (Peer.Trusted || _busy) return;
            Busy = true;
            PairRequested?.Invoke(Peer);
        };

        PointerPressed += (_, _) => SetPressed(true);
        PointerReleased += (_, _) => SetPressed(false);
        PointerCaptureLost += (_, _) => SetPressed(false);
        PointerExited += (_, _) => SetPressed(false);
    }

    /// <summary>Accoppiamento in corso su questa riga: si vede, e non si puo' avviarne un secondo.</summary>
    public bool Busy
    {
        get => _busy;
        set { if (_busy == value) return; _busy = value; InvalidateVisual(); }
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
        ctx.DrawRectangle(Palette.Brush(_pressed ? Palette.Hover : Palette.Card), null,
            new RoundedRect(row, 10));

        var avatar = new Rect(row.X + 12, row.Y + (row.Height - AvatarSize) / 2, AvatarSize, AvatarSize);
        ctx.DrawEllipse(Palette.Brush(Palette.TintFor(Peer.DeviceId)), null,
            avatar.Center, AvatarSize / 2, AvatarSize / 2);
        Ink.Draw(ctx, Palette.Initials(Peer.Label), 16, FontWeight.SemiBold, Palette.OnAccent, avatar, center: true);

        // Prima l'etichetta a destra: quel che resta e' lo spazio del nome, e cosi'
        // il nome lungo si accorcia invece di finirci sotto.
        var tagText = _busy ? L.T("mobile.pairingShort")
                    : Peer.Trusted ? L.T("mobile.peerTrusted")
                    : L.T("mobile.pair");
        var tagColor = _busy ? Palette.Info : Peer.Trusted ? Palette.Success : Palette.Accent;
        var tag = Ink.Lay(tagText, 12, FontWeight.SemiBold, Peer.Trusted && !_busy ? tagColor : Palette.OnAccent);

        var pill = new Rect(row.Right - 12 - (tag.Width + 20), row.Y + (row.Height - 26) / 2, tag.Width + 20, 26);
        if (Peer.Trusted && !_busy)
            ctx.DrawRectangle(Palette.Brush(tagColor, 40), null, new RoundedRect(pill, 13));
        else
            ctx.DrawRectangle(Palette.Brush(tagColor), null, new RoundedRect(pill, 13));
        ctx.DrawText(tag, new Point(pill.X + 10, pill.Y + (pill.Height - tag.Height) / 2));

        var textLeft = avatar.Right + 12;
        var textWidth = pill.X - textLeft - 10;
        if (textWidth <= 0) return;

        Ink.Draw(ctx, Peer.Label, 15, FontWeight.Normal, Palette.TextMain,
            new Rect(textLeft, row.Y + 9, textWidth, 20));
        Ink.Draw(ctx, L.T("mobile.peerLine", Peer.Address, DeviceIdentity.ShortFingerprint(Peer.DeviceId)),
            12, FontWeight.Normal, Palette.TextMuted,
            new Rect(textLeft, row.Y + 33, textWidth, 18));
    }
}
