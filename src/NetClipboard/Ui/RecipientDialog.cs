using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text;
using NetClipboard.Core;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Sceglie a chi mandare i file arrivati dal menu "Invia a" di Windows.
///
/// Elenca soltanto gli utenti ESTERNI, come il menu della tray: verso i propri
/// dispositivi la clipboard viaggia gia' da sola, e proporli qui rimetterebbe in
/// discussione la distinzione fra "i miei PC" e "gli altri" che tutto il resto
/// dell'interfaccia si preoccupa di rendere chiara.
///
/// Disegnata a mano come il pannello della cronologia: un ListBox owner-drawn
/// sfarfalla, perche' il disegno delle righe non passa dal doppio buffering
/// gestito (vedi ClipList in HistoryForm).
/// </summary>
public sealed class RecipientDialog : ScaledForm
{
    private static readonly Color Bg = Color.FromArgb(28, 28, 34);
    private static readonly Color Card = Color.FromArgb(38, 38, 47);
    private static readonly Color HoverBg = Color.FromArgb(48, 48, 60);
    private static readonly Color SelBg = Color.FromArgb(52, 50, 74);
    private static readonly Color TextMain = Color.FromArgb(238, 238, 244);
    private static readonly Color TextMuted = Color.FromArgb(150, 152, 165);
    private static readonly Color Accent = Color.FromArgb(120, 92, 245);
    private static readonly Color Divider = Color.FromArgb(48, 48, 59);

    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 440;
    private const int Pad = 20;
    private const int HeaderH = 74;
    private const int RowH = 56;
    private const int MaxRows = 5;

    private readonly IReadOnlyList<Peer> _peers;
    private readonly int _fileCount;
    private readonly string _sizeText;

    private readonly PeerList _list;
    private readonly Label _empty = new()
    {
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = TextMuted,
    };
    private readonly Label _note = new() { ForeColor = TextMuted };
    private readonly Button _send = new() { DialogResult = DialogResult.OK };
    private readonly Button _cancel = new() { DialogResult = DialogResult.Cancel };

    private Font _fTitle = null!, _fSub = null!, _fName = null!, _fDetail = null!, _fAvatar = null!, _fNote = null!;

    /// <summary>Destinatario scelto, valorizzato solo se la finestra esce con OK.</summary>
    public Peer? Chosen { get; private set; }

    public RecipientDialog(IReadOnlyList<Peer> peers, int fileCount, string sizeText)
    {
        _peers = peers;
        _fileCount = fileCount;
        _sizeText = sizeText;

        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = Bg;
        ForeColor = TextMain;
        DoubleBuffered = true;

        Text = L.T("recipient.title");
        _empty.Text = L.T("recipient.none");
        _note.Text = L.T("recipient.note");
        _send.Text = L.T("recipient.send");
        _cancel.Text = L.T("common.cancel");

        foreach (var b in new[] { _send, _cancel })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(58, 58, 70);
        }
        // Un pulsante piatto disabilitato conserva il colore di sfondo: senza questo
        // "Invia" resterebbe acceso anche quando non c'e' nessuno a cui mandare.
        _send.EnabledChanged += (_, _) =>
        {
            _send.BackColor = _send.Enabled ? Accent : Color.FromArgb(52, 52, 62);
            _send.ForeColor = _send.Enabled ? Color.White : TextMuted;
        };
        _send.BackColor = Accent;

        _list = new PeerList { BackColor = Bg };
        _list.SetItems(peers);
        _list.DrawRow += DrawPeerRow;
        _list.Activated += () => { if (Confirm()) { DialogResult = DialogResult.OK; Close(); } };
        _list.SelectionChanged += () => _send.Enabled = _list.Selected != null;

        _empty.Visible = peers.Count == 0;
        _list.Visible = peers.Count > 0;
        _note.Visible = peers.Count > 0;   // a elenco vuoto non c'e' nessun destinatario a cui riferirsi
        _send.Enabled = _list.Selected != null;

        _send.Click += (_, _) => { if (!Confirm()) DialogResult = DialogResult.None; };
        AcceptButton = _send;
        CancelButton = _cancel;

        Controls.AddRange(new Control[] { _list, _empty, _note, _send, _cancel });
        Paint += DrawChrome;
    }

    private bool Confirm()
    {
        Chosen = _list.Selected;
        return Chosen != null;
    }

    protected override Font CreateBaseFont() => PxFont("Segoe UI", 12f);

    protected override void ApplyLayout()
    {
        DisposeFonts();
        _fTitle = PxFont("Segoe UI Semibold", 15f);
        _fSub = PxFont("Segoe UI", 11.5f);
        _fName = PxFont("Segoe UI Semibold", 12.5f);
        _fDetail = PxFont("Segoe UI", 10.5f);
        _fAvatar = PxFont("Segoe UI Semibold", 13f);
        _fNote = PxFont("Segoe UI", 10f);

        _note.Font = _fNote;
        _empty.Font = _fSub;
        _list.ItemHeight = P(RowH);

        var full = ClientW - 2 * Pad;
        var y = HeaderH + 6;

        var rows = Math.Clamp(_peers.Count, 1, MaxRows);
        var listH = P(RowH) * rows;

        if (_peers.Count > 0) { _list.SetBounds(P(Pad - 6), P(y), P(full + 12), listH); _empty.Bounds = Rectangle.Empty; }
        else { _empty.SetBounds(P(Pad), P(y), P(full), P(RowH)); _list.Bounds = Rectangle.Empty; }
        y += (int)Math.Round(listH / ScaleFactor) + 12;

        if (_peers.Count > 0) { _note.SetBounds(P(Pad), P(y), P(full), P(30)); y += 36; }
        else { _note.Bounds = Rectangle.Empty; y += 8; }   // nessuna posizione residua

        _cancel.SetBounds(P(ClientW - Pad - 104), P(y), P(104), P(34));
        _send.SetBounds(_cancel.Left - P(112), P(y), P(104), P(34));
        y += 34 + Pad;

        ClientSize = new Size(P(ClientW), P(y));
    }

    /// <summary>Intestazione: cosa si sta mandando, prima ancora di scegliere a chi.</summary>
    private void DrawChrome(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var header = new Rectangle(0, 0, Width, P(HeaderH));
        using (var hb = new SolidBrush(Color.FromArgb(33, 33, 41)))
            g.FillRectangle(hb, header);
        using (var line = new Pen(Divider))
            g.DrawLine(line, 0, header.Bottom - 1, Width, header.Bottom - 1);

        // Riquadro col numero di file: dice subito la sostanza dell'invio.
        var box = new Rectangle(P(Pad), P(18), P(38), P(38));
        using (var grad = new LinearGradientBrush(box,
                   Color.FromArgb(240, 170, 60), Color.FromArgb(220, 130, 40),
                   LinearGradientMode.ForwardDiagonal))
            g.FillRoundedRect(grad, box, P(9));
        TextRenderer.DrawText(g, _fileCount.ToString(), _fAvatar, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var textLeft = box.Right + P(14);
        TextRenderer.DrawText(g, L.T("recipient.heading"), _fTitle,
            new Rectangle(textLeft, P(16), Width - textLeft - P(Pad), P(22)), TextMain,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, L.T("recipient.subtitle", _fileCount, _sizeText), _fSub,
            new Rectangle(textLeft, P(40), Width - textLeft - P(Pad), P(20)), TextMuted,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    private void DrawPeerRow(Graphics g, Peer peer, Rectangle bounds, bool selected, bool hovered)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var row = new Rectangle(bounds.Left + P(6), bounds.Top + P(4), bounds.Width - P(12), bounds.Height - P(8));
        using (var rb = new SolidBrush(selected ? SelBg : hovered ? HoverBg : Card))
            g.FillRoundedRect(rb, row, P(9));
        if (selected)
            using (var acc = new SolidBrush(Accent))
                g.FillRoundedRect(acc, new Rectangle(row.Left, row.Top + P(6), P(3), row.Height - P(12)), P(2));

        // Iniziali su tinta stabile: lo stesso destinatario ha sempre lo stesso
        // colore, cosi' si riconosce a colpo d'occhio senza leggere.
        var av = P(34);
        var avatar = new Rectangle(row.Left + P(12), row.Top + (row.Height - av) / 2, av, av);
        var tint = TintFor(peer.DeviceId);
        using (var ab = new SolidBrush(tint))
            g.FillEllipse(ab, avatar);
        TextRenderer.DrawText(g, Initials(peer.Label), _fAvatar, avatar, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var left = avatar.Right + P(12);
        var w = row.Right - left - P(12);
        TextRenderer.DrawText(g, peer.Label, _fName,
            new Rectangle(left, row.Top + P(8), w, P(20)), TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, L.T("recipient.peerDetail", peer.Address), _fDetail,
            new Rectangle(left, row.Top + P(28), w, P(18)), TextMuted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    private static string Initials(string label)
    {
        var parts = label.Split(new[] { ' ', '·', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
    }

    /// <summary>Tinta derivata dall'identita': stabile fra sessioni e fra dispositivi.</summary>
    private static Color TintFor(string deviceId)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId ?? ""));
        var hue = h[0] / 255.0 * 360.0;
        return FromHsl(hue, 0.45, 0.48);
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
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fSub?.Dispose(); _fName?.Dispose();
        _fDetail?.Dispose(); _fAvatar?.Dispose(); _fNote?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeFonts();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// Elenco dei destinatari disegnato da noi. Stesso motivo di ClipList: su un
    /// ListBox owner-drawn il disegno arriva dall'HDC dello schermo e non dal back
    /// buffer, quindi lampeggia a ogni ridisegno.
    /// </summary>
    private sealed class PeerList : Control
    {
        private readonly List<Peer> _items = new();
        private int _selected = -1;
        private int _hover = -1;
        private int _scroll;

        public event Action<Graphics, Peer, Rectangle, bool, bool>? DrawRow;
        public event Action? Activated;
        public event Action? SelectionChanged;

        public PeerList()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.Opaque
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            TabStop = true;
        }

        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int ItemHeight { get; set; } = 56;

        public Peer? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        public void SetItems(IEnumerable<Peer> peers)
        {
            _items.Clear();
            _items.AddRange(peers);
            _selected = _items.Count > 0 ? 0 : -1;
            Invalidate();
        }

        private Rectangle RowRect(int i) => new(0, i * ItemHeight - _scroll, ClientSize.Width, ItemHeight);

        private int IndexAt(Point p)
        {
            if (ItemHeight <= 0) return -1;
            var i = (p.Y + _scroll) / ItemHeight;
            return i >= 0 && i < _items.Count ? i : -1;
        }

        private int MaxScroll => Math.Max(0, _items.Count * ItemHeight - ClientSize.Height);

        private void Select(int i)
        {
            if (i < 0 || i >= _items.Count || i == _selected) return;
            _selected = i;

            var top = i * ItemHeight;
            if (top < _scroll) _scroll = top;
            else if (top + ItemHeight > _scroll + ClientSize.Height)
                _scroll = top + ItemHeight - ClientSize.Height;
            _scroll = Math.Clamp(_scroll, 0, MaxScroll);

            Invalidate();
            SelectionChanged?.Invoke();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var i = IndexAt(e.Location);
            if (i != _hover)
            {
                var prev = _hover;
                _hover = i;
                if (prev >= 0) Invalidate(RowRect(prev));
                if (_hover >= 0) Invalidate(RowRect(_hover));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hover >= 0) { var p = _hover; _hover = -1; Invalidate(RowRect(p)); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var i = IndexAt(e.Location);
            if (i >= 0) Select(i);
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (IndexAt(e.Location) >= 0) Activated?.Invoke();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var before = _scroll;
            _scroll = Math.Clamp(_scroll - e.Delta / 120 * ItemHeight, 0, MaxScroll);
            if (_scroll != before) Invalidate();
            base.OnMouseWheel(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up) { Select(_selected - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Select(_selected + 1); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var bg = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(bg, e.ClipRectangle);

            if (_items.Count == 0 || ItemHeight <= 0) return;

            var first = Math.Max(0, (_scroll + e.ClipRectangle.Top) / ItemHeight);
            var last = Math.Min(_items.Count - 1, (_scroll + e.ClipRectangle.Bottom) / ItemHeight);
            for (var i = first; i <= last; i++)
                DrawRow?.Invoke(e.Graphics, _items[i], RowRect(i), i == _selected, i == _hover);
        }
    }
}
