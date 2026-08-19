using System.Drawing.Drawing2D;
using System.IO;
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
/// gestito (vedi ClipList in HistoryForm). I colori arrivano da <see cref="Theme"/>,
/// che segue la modalita' chiara/scura di Windows.
/// </summary>
public sealed class RecipientDialog : ScaledForm
{
    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 460;
    private const int Pad = 20;
    private const int HeaderH = 80;
    private const int BadgeSz = 46;
    private const int RowH = 56;
    private const int MaxRows = 5;

    /// <summary>Diametro del pallino del conteggio (0,46 del lato del segno).</summary>
    private const int ChipD = 21;

    /// <summary>
    /// Rientro dell'elenco: cade sul centro del segno dei file in intestazione,
    /// cosi' l'elenco pende da li' invece di sfalsarsi.
    /// </summary>
    private const int ListIndent = Pad + BadgeSz / 2;

    /// <summary>Oltre questa lunghezza il nome di un file viene accorciato nel mezzo.</summary>
    private const int MaxNameChars = 26;

    private readonly IReadOnlyList<Peer> _peers;
    private readonly IReadOnlyList<string> _names;
    private readonly int _fileCount;
    private readonly int _dirCount;
    private readonly string _sizeText;

    private readonly PeerList _list;
    private readonly Label _empty = new() { TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _note = new();
    private readonly Button _send = new() { DialogResult = DialogResult.OK };
    private readonly Button _cancel = new() { DialogResult = DialogResult.Cancel };

    private Font _fTitle = null!, _fSub = null!, _fName = null!, _fDetail = null!,
                 _fAvatar = null!, _fNote = null!, _fCount = null!,
                 _fButton = null!, _fButtonStrong = null!;

    /// <summary>Destinatario scelto, valorizzato solo se la finestra esce con OK.</summary>
    public Peer? Chosen { get; private set; }

    public RecipientDialog(IReadOnlyList<Peer> peers, FileOffer offer, string sizeText)
    {
        _peers = peers;
        _names = offer.TopLevelNames.ToList();
        _fileCount = offer.FileCount;
        _dirCount = offer.DirCount;
        _sizeText = sizeText;

        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
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
        }
        // Un pulsante piatto disabilitato conserva il colore di sfondo: senza questo
        // "Invia" resterebbe acceso anche quando non c'e' nessuno a cui mandare.
        _send.EnabledChanged += (_, _) => PaintSendButton();

        _list = new PeerList();
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
        Theme.Attach(this, ApplyTheme);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.TextMain;
        _list.BackColor = Theme.Bg;
        _empty.ForeColor = Theme.TextMuted;
        _note.ForeColor = Theme.TextMuted;
        _cancel.BackColor = Theme.ButtonFace;
        _cancel.ForeColor = Theme.ButtonText;
        _cancel.FlatAppearance.BorderColor = Theme.Divider;
        _cancel.FlatAppearance.BorderSize = 1;
        PaintSendButton();
    }

    private void PaintSendButton()
    {
        _send.BackColor = _send.Enabled ? Theme.Accent : Theme.ButtonDisabledFace;
        _send.ForeColor = _send.Enabled ? Theme.OnAccent : Theme.TextSpent;
        _send.FlatAppearance.BorderColor = _send.Enabled ? Theme.Accent : Theme.Divider;
        _send.FlatAppearance.BorderSize = _send.Enabled ? 0 : 1;
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
        _fCount = PxFont("Segoe UI Semibold", 10.5f);

        // I pulsanti sono piu' alti dello standard di Windows: con il corpo
        // standard (9 pt) il testo ci galleggia dentro. Un filo piu' grande, e
        // semibold su quello che porta avanti l'azione.
        _fButton = PxFont("Segoe UI", 13f);
        _fButtonStrong = PxFont("Segoe UI Semibold", 13f);

        _cancel.Font = _fButton;
        _send.Font = _fButtonStrong;
        _note.Font = _fNote;
        _empty.Font = _fSub;
        _list.ItemHeight = P(RowH);

        var y = HeaderH + 6;

        // L'elenco sborda di 6 px logici oltre il rientro: le righe si disegnano
        // con quello stesso scarto, cosi' il bordo della scheda cade sul rientro.
        var listX = ListIndent - 6;
        var listW = ClientW - Pad + 6 - listX;
        var innerW = ClientW - Pad - ListIndent;

        var rows = Math.Clamp(_peers.Count, 1, MaxRows);
        var listH = P(RowH) * rows;

        if (_peers.Count > 0) { _list.SetBounds(P(listX), P(y), P(listW), listH); _empty.Bounds = Rectangle.Empty; }
        else { _empty.SetBounds(P(ListIndent), P(y), P(innerW), P(RowH)); _list.Bounds = Rectangle.Empty; }
        y += (int)Math.Round(listH / ScaleFactor) + 12;

        if (_peers.Count > 0) { _note.SetBounds(P(ListIndent), P(y), P(innerW), P(30)); y += 36; }
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
        using (var hb = new SolidBrush(Theme.HeaderBg))
            g.FillRectangle(hb, header);
        using (var line = new Pen(Theme.Divider))
            g.DrawLine(line, 0, header.Bottom - 1, Width, header.Bottom - 1);

        var box = new Rectangle(P(Pad), P((HeaderH - BadgeSz) / 2), P(BadgeSz), P(BadgeSz));
        DrawFilesBadge(g, box);

        var textLeft = box.Right + P(14);
        var textW = Width - textLeft - P(Pad);
        TextRenderer.DrawText(g, L.T("recipient.heading"), _fTitle,
            new Rectangle(textLeft, P(19), textW, P(24)), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, FittingSummary(g, textW), _fSub,
            new Rectangle(textLeft, P(45), textW, P(20)), Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    /// <summary>
    /// Cosa si sta mandando, detto con i nomi veri: "Pippo.pdf, pluto.txt e altri
    /// 15 file (2 MB)". Un conteggio nudo non dice se hai preso i file giusti.
    /// </summary>
    private string Summary(int nameBudget)
    {
        var n = _names.Select(x => Shorten(x, nameBudget)).ToList();
        return n.Count switch
        {
            0 => L.T("recipient.subtitle", _fileCount, _sizeText),   // nessun nome leggibile: si ripiega sul conteggio
            1 => L.T("recipient.summaryOne", n[0], _sizeText),
            2 => L.T("recipient.summaryTwo", n[0], n[1], _sizeText),
            3 => L.T("recipient.summaryThree", n[0], n[1], n[2], _sizeText),
            _ => L.T(_dirCount > 0 ? "recipient.summaryManyItems" : "recipient.summaryManyFiles",
                     n[0], n[1], n.Count - 2, _sizeText),
        };
    }

    /// <summary>
    /// La versione piu' lunga che entra nella riga. I nomi si accorciano finche'
    /// serve, e se non basta si ripiega sul conteggio: la dimensione in fondo non
    /// deve mai finire tagliata, perche' e' l'unica cosa che dice quanto pesa.
    /// </summary>
    private string FittingSummary(Graphics g, int width)
    {
        var budget = MaxNameChars;
        var text = Summary(budget);
        while (budget > 8 && Wider(g, text, width))
            text = Summary(budget -= 3);
        return Wider(g, text, width) ? L.T("recipient.subtitle", _fileCount, _sizeText) : text;
    }

    private bool Wider(Graphics g, string text, int width) =>
        TextRenderer.MeasureText(g, text, _fSub, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding).Width > width;

    /// <summary>
    /// Nome accorciato nel mezzo, conservando l'estensione: e' l'estensione a dire
    /// di che cosa si tratta, quindi e' l'ultima cosa da tagliare.
    /// </summary>
    private static string Shorten(string name, int max)
    {
        if (name.Length <= max) return name;
        var ext = Path.GetExtension(name);
        if (ext.Length > 6) ext = "";                    // non e' un'estensione, e' parte del nome
        var keep = max - ext.Length - 1;
        return keep < 4
            ? name[..(max - 1)] + Ellipsis
            : name[..keep] + Ellipsis + ext;
    }

    private const string Ellipsis = "…";

    /// <summary>
    /// Pila di fogli con il numero di file in un pallino: si vede a colpo d'occhio
    /// che si sta mandando roba, e quanta, senza leggere niente.
    /// </summary>
    private void DrawFilesBadge(Graphics g, Rectangle box)
    {
        float w = box.Width;
        float cw = w * 0.60f, ch = w * 0.78f;   // il foglio in primo piano
        float step = w * 0.10f;                 // scarto fra un foglio e l'altro
        float radius = Math.Max(2f, w * 0.09f);

        // I due fogli dietro, sempre piu' smorzati: danno spessore alla pila senza
        // rubare attenzione al numero.
        for (var i = 2; i >= 1; i--)
        {
            var back = new RectangleF(box.X + step * i, box.Y + step * (2 - i), cw, ch);
            using var b = new SolidBrush(Color.FromArgb(i == 2 ? 80 : 150, Theme.FileWarmA));
            g.FillRoundedRect(b, back, radius);
        }

        // Il foglio davanti ha l'angolo tagliato invece che stondato: e' la piega
        // a far leggere "documento", e su un angolo tondo sembrerebbe appiccicata.
        var front = new RectangleF(box.X, box.Y + step * 2, cw, ch);
        var fold = cw * 0.32f;
        using (var path = DocumentPath(front, radius, fold))
        using (var grad = new LinearGradientBrush(
                   new RectangleF(front.X, front.Y, front.Width + 1, front.Height + 1),
                   Theme.FileWarmA, Theme.FileWarmB, LinearGradientMode.ForwardDiagonal))
            g.FillPath(grad, path);

        using (var lighter = new SolidBrush(Color.FromArgb(105, 255, 255, 255)))
            g.FillPolygon(lighter, new[]
            {
                new PointF(front.Right - fold, front.Y),
                new PointF(front.Right, front.Y + fold),
                new PointF(front.Right - fold, front.Y + fold),
            });

        // Righe di "testo" sul foglio: solo se c'e' spazio perche' si distinguano.
        if (front.Height >= 30)
        {
            var lh = Math.Max(1f, w * 0.045f);
            var lx = front.X + cw * 0.18f;
            var ly = front.Y + ch * 0.46f;
            using var ink = new SolidBrush(Color.FromArgb(115, 255, 255, 255));
            foreach (var factor in new[] { 0.62f, 0.62f, 0.40f })
            {
                g.FillRoundedRect(ink, new RectangleF(lx, ly, cw * factor, lh), lh / 2);
                ly += lh * 2.6f;
            }
        }

        // Pallino con il conteggio, appoggiato in basso a destra sulla pila.
        var d = w * ((float)ChipD / BadgeSz);
        var dot = new RectangleF(box.Right - d, box.Bottom - d, d, d);
        using (var ring = new SolidBrush(Theme.HeaderBg))
            g.FillEllipse(ring, RectangleF.Inflate(dot, w * 0.03f, w * 0.03f));
        using (var fill = new SolidBrush(Theme.Accent))
            g.FillEllipse(fill, dot);
        TextRenderer.DrawText(g, CountLabel(), _fCount, Rectangle.Round(dot), Theme.OnAccent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    /// <summary>Sagoma del foglio: angoli stondati, tranne quello in alto a destra, piegato.</summary>
    private static GraphicsPath DocumentPath(RectangleF r, float radius, float fold)
    {
        var d = Math.Max(0.5f, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddLine(r.Right - fold, r.Y, r.Right, r.Y + fold);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Nel pallino ci stanno due cifre: oltre le 99 si dice "tante".</summary>
    private string CountLabel()
    {
        var n = _fileCount > 0 ? _fileCount : _names.Count;
        return n > 99 ? L.T("recipient.countOverflow") : n.ToString();
    }

    private void DrawPeerRow(Graphics g, Peer peer, Rectangle bounds, bool selected, bool hovered)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var row = new Rectangle(bounds.Left + P(6), bounds.Top + P(4), bounds.Width - P(12), bounds.Height - P(8));
        using (var rb = new SolidBrush(selected ? Theme.Sel : hovered ? Theme.Hover : Theme.Card))
            g.FillRoundedRect(rb, row, P(9));
        if (selected)
            using (var acc = new SolidBrush(Theme.Accent))
                g.FillRoundedRect(acc, new Rectangle(row.Left, row.Top + P(6), P(3), row.Height - P(12)), P(2));

        // Iniziali su tinta stabile: lo stesso destinatario ha sempre lo stesso
        // colore, cosi' si riconosce a colpo d'occhio senza leggere.
        var av = P(34);
        var avatar = new Rectangle(row.Left + P(12), row.Top + (row.Height - av) / 2, av, av);
        using (var ab = new SolidBrush(TintFor(peer.DeviceId)))
            g.FillEllipse(ab, avatar);
        TextRenderer.DrawText(g, Initials(peer.Label), _fAvatar, avatar, Theme.OnAccent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var left = avatar.Right + P(12);
        var w = row.Right - left - P(12);
        TextRenderer.DrawText(g, peer.Label, _fName,
            new Rectangle(left, row.Top + P(8), w, P(20)), Theme.TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, L.T("recipient.peerDetail", peer.Address), _fDetail,
            new Rectangle(left, row.Top + P(28), w, P(18)), Theme.TextMuted,
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
        return FromHsl(hue, 0.45, Theme.AvatarLightness);
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
        _fTitle?.Dispose(); _fSub?.Dispose(); _fName?.Dispose(); _fDetail?.Dispose();
        _fAvatar?.Dispose(); _fNote?.Dispose(); _fCount?.Dispose();
        _fButton?.Dispose(); _fButtonStrong?.Dispose();
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
