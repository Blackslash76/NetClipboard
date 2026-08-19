using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.IO;
using NetClipboard.Core;

namespace NetClipboard.Ui;

/// <summary>
/// Popup della cronologia condivisa (il "nostro Win+V" cross-device).
/// Interamente DPI-aware: ogni misura e ogni font vengono scalati in base al
/// DPI del monitor, così il testo non viene tagliato con lo scaling di Windows
/// (125% / 150% ecc.).
/// </summary>
public sealed class HistoryForm : Form
{
    // Misure logiche (a 96 DPI); vengono scalate da _scale.
    //
    // Il pannello e' uno strumento di scelta rapida, non una pagina da leggere:
    // ogni pixel speso in decorazione e' una riga in meno visibile. Intestazione
    // ridotta all'osso, niente piu' piede, righe piu' compatte.
    private const int HeaderH = 36;
    private const int Radius = 12;
    private const int RowH = 50;
    private const int AvatarSz = 32;

    private readonly ClipboardHistory _history;
    private readonly AppConfig _config;
    private readonly ClipList _list;
    private readonly Dictionary<string, Image> _thumbCache = new();

    /// <summary>Righe gia' ridisegnate come scadute: si smorzano una volta sola.</summary>
    private readonly HashSet<string> _expired = new();

    /// <summary>
    /// Fa scorrere gli anelli di scadenza mentre la finestra e' aperta, e smorza
    /// le voci esterne appena scadono invece di lasciarle attive fino alla
    /// riapertura. Gira solo a finestra visibile e solo finche' serve.
    /// </summary>
    private readonly System.Windows.Forms.Timer _expiryTick = new() { Interval = 1000 };

    private float _scale = 1f;
    private Font _fTitle = null!, _fHint = null!,
                 _fPreview = null!, _fMeta = null!, _fBadge = null!;

    private IntPtr _target;
    /// <summary>Finestra che aveva il fuoco quando si è aperto il popup (per incollarci).</summary>
    public IntPtr TargetWindow => _target;

    public event Action<HistoryItem>? ItemChosen;

    public HistoryForm(ClipboardHistory history, AppConfig config)
    {
        _history = history;
        _config = config;

        AutoScaleMode = AutoScaleMode.None; // scaliamo noi, manualmente
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Theme.Bg;

        _list = new ClipList
        {
            BackColor = Theme.Bg,
            ForeColor = Theme.TextMain,
        };
        _list.DrawRow += DrawRow;
        _list.KeyDown += OnKeyDown;
        _list.MouseUp += OnMouseUp;
        Controls.Add(_list);

        Deactivate += (_, _) => Hide();
        Paint += OnPaintChrome;
        Theme.Attach(this, ApplyTheme);

        _expiryTick.Tick += (_, _) =>
        {
            if (!Visible) { _expiryTick.Stop(); return; }

            // Si tocca solo cio' che cambia: delle righe ancora vive si ridisegna
            // il singolo rettangolo, e quelle appena scadute una volta sola.
            var alive = false;
            var expired = false;
            var rows = new List<Rectangle>();

            for (var i = _list.Items.Count - 1; i >= 0; i--)
            {
                if (_list.Items[i] is not HistoryItem it || !it.FromExternal) continue;
                if (RemainingFraction(it) <= 0)
                {
                    // Appena scaduta va ridisegnata una volta, per passare a spenta.
                    if (!_expired.Add(it.Id)) continue;
                    expired = true;
                    continue;
                }
                alive = true;
                rows.Add(_list.GetItemRectangle(i));
            }

            // Con la lista disegnata da noi basta invalidare: WinForms dipinge su
            // back buffer e riversa in una passata sola, senza cancellazione prima.
            if (expired) _list.Invalidate();
            else foreach (var r in rows) _list.Invalidate(r);

            if (!alive) _expiryTick.Stop();
        };
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Bg;
        _list.BackColor = Theme.Bg;
        _list.ForeColor = Theme.TextMain;
    }

    private int P(double v) => (int)Math.Round(v * _scale);

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; } // ombra
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyScale();
    }

    private void ApplyScale()
    {
        var scale = DeviceDpi / 96f;
        if (Math.Abs(scale - _scale) < 0.01f && _fTitle != null)
        {
            ApplyLayout();
            return;
        }
        _scale = scale;

        DisposeFonts();
        _fTitle = PxFont("Segoe UI Semibold", 13f);
        _fHint = PxFont("Segoe UI", 10f);
        _fPreview = PxFont("Segoe UI", 12.5f);
        _fMeta = PxFont("Segoe UI", 10f);
        _fBadge = PxFont("Segoe UI", 13.5f, FontStyle.Bold);

        ApplyLayout();
    }

    private Font PxFont(string family, float px, FontStyle style = FontStyle.Regular) =>
        new(family, px * _scale, style, GraphicsUnit.Pixel);

    private void ApplyLayout()
    {
        // L'altezza della lista e' un multiplo esatto della riga: niente elemento
        // tagliato a meta' in fondo. Siccome la rotella scorre di una riga per
        // volta, l'allineamento regge anche durante lo scorrimento.
        // Il numero di righe lo decide l'utente; si ricalcola a ogni apertura,
        // percio' cambiarlo in Impostazioni ha effetto subito.
        var visibleRows = Math.Clamp(_config.HistoryVisibleRows,
                                     AppConfig.MinVisibleRows, AppConfig.MaxVisibleRows);
        var listH = P(RowH) * visibleRows;

        _list.ItemHeight = P(RowH);
        Size = new Size(P(420), P(HeaderH) + listH + P(6));
        _list.SetBounds(P(6), P(HeaderH), Width - P(12), listH);
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), P(Radius));
        Region = new Region(path);
    }

    public void ShowNearCursor()
    {
        _target = NativePaste.GetForegroundWindow(); // la finestra dove incollare
        _ = Handle;      // forza la creazione dell'handle (per avere il DPI)
        ApplyScale();
        _history.PurgeExpired();
        Reload();
        _expiryTick.Start();

        var pos = Cursor.Position;
        var screen = Screen.FromPoint(pos).WorkingArea;
        var x = Math.Clamp(pos.X, screen.Left + 8, screen.Right - Width - 8);
        var y = Math.Clamp(pos.Y, screen.Top + 8, screen.Bottom - Height - 8);
        Location = new Point(x, y);

        Show();
        Activate();
        _list.Focus();
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void Reload()
    {
        _list.SetItems(_history.Items
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.TimestampUtc));
    }

    private void ChooseSelected()
    {
        // Le righe spente restano selezionabili ma non fanno nulla: sono una
        // traccia di cio' che e' passato, non un contenuto ancora incollabile.
        if (_list.SelectedItem is HistoryItem item && !ClipboardHistory.IsSpent(item))
        {
            Hide();
            ItemChosen?.Invoke(item);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter: ChooseSelected(); e.Handled = true; break;
            case Keys.Escape: Hide(); e.Handled = true; break;
            case Keys.Delete:
                if (_list.SelectedItem is HistoryItem del) { _history.Remove(del.Id); Reload(); }
                e.Handled = true; break;
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var i = _list.IndexFromPoint(e.Location);
            if (i >= 0) { _list.SelectedIndex = i; ChooseSelected(); } // singolo click = incolla
            return;
        }
        if (e.Button != MouseButtons.Right) return;
        var idx = _list.IndexFromPoint(e.Location);
        if (idx < 0) return;
        _list.SelectedIndex = idx;
        if (_list.SelectedItem is not HistoryItem item) return;

        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T(item.Pinned ? "history.unpin" : "history.pin"), null, (_, _) => { _history.TogglePin(item.Id); Reload(); });
        menu.Items.Add(L.T("common.delete"), null, (_, _) => { _history.Remove(item.Id); Reload(); });
        menu.Show(_list, e.Location);
    }

    private void OnPaintChrome(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Barra sobria invece della fascia colorata: il colore serve a evidenziare
        // il contenuto, non l'intestazione. Resta un solo accento, il segno a
        // sinistra del nome, che fa da marchio senza rubare spazio.
        var headerRect = new Rectangle(0, 0, Width, P(HeaderH));
        using (var hb = new SolidBrush(Theme.HeaderBg))
            g.FillRectangle(hb, headerRect);
        using (var line = new Pen(Theme.Divider))
            g.DrawLine(line, 0, headerRect.Bottom - 1, Width, headerRect.Bottom - 1);

        var mark = new Rectangle(P(12), (P(HeaderH) - P(14)) / 2, P(14), P(14));
        using (var grad = new LinearGradientBrush(mark, Theme.Accent, Theme.AccentAlt, LinearGradientMode.ForwardDiagonal))
            g.FillRoundedRect(grad, mark, P(4));

        TextRenderer.DrawText(g, L.T("app.name"), _fTitle,
            new Rectangle(mark.Right + P(8), 0, P(180), P(HeaderH)), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, L.T("history.hint"), _fHint,
            new Rectangle(Width - P(200), 0, P(188), P(HeaderH)), Theme.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawRow(Graphics g, HistoryItem item, Rectangle bounds, bool selected, bool hovered)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var row = new Rectangle(bounds.Left + P(5), bounds.Top + P(3), bounds.Width - P(10), bounds.Height - P(6));
        var face = selected ? Theme.Sel : hovered ? Theme.Hover : Theme.Card;
        using (var rb = new SolidBrush(face))
            g.FillRoundedRect(rb, row, P(8));
        if (selected)
            using (var accent = new SolidBrush(Theme.Accent))
                g.FillRoundedRect(accent, new Rectangle(row.Left, row.Top + P(5), P(3), row.Height - P(10)), P(2));

        var iconSz = P(AvatarSz);
        var slot = new Rectangle(row.Left + P(10), row.Top + (row.Height - iconSz) / 2, iconSz, iconSz);

        // Sugli esterni l'avatar si stringe per lasciare posto all'anello di
        // scadenza che lo circonda: l'informazione sta addosso al contenuto,
        // invece che in un angolo staccato della riga.
        var icon = item.FromExternal ? Rectangle.Inflate(slot, -P(4), -P(4)) : slot;
        var spent = ClipboardHistory.IsSpent(item);
        var thumb = GetThumb(item, icon.Width);
        if (thumb != null)
        {
            using var clip = new GraphicsPath();
            clip.AddRoundedRectangle(icon, P(7));
            var saved = g.Clip;
            g.SetClip(clip, CombineMode.Replace);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(thumb, icon);
            g.Clip = saved;
        }
        else
        {
            var (c1, c2, glyph) = BadgeStyle(item);
            using (var grad = new LinearGradientBrush(icon, c1, c2, LinearGradientMode.ForwardDiagonal))
                g.FillRoundedRect(grad, icon, P(7));
            TextRenderer.DrawText(g, glyph, _fBadge, icon, Theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Anello solo finche' il conto alla rovescia ha senso: una riga spenta
        // porta l'etichetta, non un anello vuoto.
        if (item.FromExternal && !spent)
            DrawExpiryRing(g, slot, RemainingFraction(item));

        // L'avatar di una riga spenta si smorza, cosi' la riga si legge come
        // disattivata gia' dalla coda dell'occhio, prima di leggere l'etichetta.
        if (spent)
            using (var veil = new SolidBrush(Color.FromArgb(150, Theme.Card)))
                g.FillRectangle(veil, slot);

        var textLeft = slot.Right + P(10);
        var textWidth = row.Right - textLeft - P(10);

        // Etichetta di stato a destra: la riga resta visibile come traccia di cio'
        // che e' passato, ma si vede che non e' piu' utilizzabile.
        if (spent)
        {
            var tag = L.T(item.Used ? "history.used" : "history.expired");
            var tagW = TextRenderer.MeasureText(tag, _fMeta).Width + P(4);
            TextRenderer.DrawText(g, tag, _fMeta,
                new Rectangle(row.Right - P(10) - tagW, row.Top + P(6), tagW, P(18)), Theme.TextSpent,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
            textWidth -= tagW + P(8);
        }

        TextRenderer.DrawText(g, item.Preview, _fPreview,
            new Rectangle(textLeft, row.Top + P(6), textWidth, P(18)), spent ? Theme.TextSpent : Theme.TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);

        var pin = item.Pinned ? "📌 " : "";
        var toFetch = item.Kind == PayloadKind.Files && !item.IsLocalOffer
            && (item.LocalRootPaths == null || item.LocalRootPaths.Count == 0) ? L.T("history.toDownload") : "";
        var origin = item.IsLocal ? L.T("history.thisPc") : item.Origin;
        if (item.FromExternal) origin = L.T("history.external", origin);
        var meta = L.T("history.meta", pin, origin, LocalTime(item.TimestampUtc), toFetch);
        TextRenderer.DrawText(g, meta, _fMeta,
            new Rectangle(textLeft, row.Top + P(24), textWidth, P(16)), spent ? Theme.TextSpent : Theme.TextMuted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    /// <summary>Quanta vita resta a un contenuto esterno, da 1 (appena arrivato) a 0 (scaduto).</summary>
    private static double RemainingFraction(HistoryItem item)
    {
        var total = ClipboardHistory.ExternalLifetime(item.Kind).TotalMilliseconds;
        if (total <= 0) return 0;
        var left = total - (DateTime.UtcNow - item.TimestampUtc).TotalMilliseconds;
        return Math.Clamp(left / total, 0, 1);
    }

    /// <summary>
    /// Anello di scadenza attorno all'avatar: si consuma in senso orario col tempo
    /// residuo e vira all'arancione sull'ultimo quarto, quando conviene sbrigarsi.
    /// </summary>
    private static void DrawExpiryRing(Graphics g, Rectangle box, double fraction)
    {
        var color = fraction <= 0.25 ? Theme.Warn : Theme.Info;

        var thickness = Math.Max(2f, box.Width / 14f);
        var arc = Rectangle.Inflate(box, (int)(-thickness / 2), (int)(-thickness / 2));

        var saved = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var track = new Pen(Color.FromArgb(60, Theme.TextMuted), thickness))
            g.DrawEllipse(track, arc);

        if (fraction > 0)
            using (var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, -90f, (float)(360.0 * fraction));

        g.SmoothingMode = saved;
    }

    private static (Color, Color, string) BadgeStyle(HistoryItem item) => item.Kind switch
    {
        PayloadKind.Text => (Theme.TextKindA, Theme.TextKindB, "T"),
        PayloadKind.Files => (Theme.FileWarmA, Theme.FileWarmB, item.DirCount > 0 ? "🗀" : "🗎"),
        _ => (Theme.OtherKindA, Theme.OtherKindB, "?"),
    };

    private Image? GetThumb(HistoryItem item, int size)
    {
        if (item.Kind != PayloadKind.Image || item.BlobFile == null) return null;
        var key = item.Id + "@" + size;
        if (_thumbCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            // I blob sono cifrati a riposo: si passa dalla cronologia, che ha la
            // chiave. Image.FromFile qui non funzionerebbe piu'.
            var png = _history.ReadBlob(item);
            if (png == null) return null;
            using var ms = new MemoryStream(png);
            using var src = Image.FromStream(ms);
            var thumb = new Bitmap(size, size);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            _thumbCache[key] = thumb;
            return thumb;
        }
        catch { return null; }
    }

    private static string LocalTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTime.Now - local;
        if (delta.TotalSeconds < 60) return L.T("time.now");
        if (delta.TotalMinutes < 60) return L.T("time.minutesAgo", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return L.T("time.hoursAgo", (int)delta.TotalHours);
        return local.ToString(L.T("time.dateFormat"));
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DisposeFonts()
    {
        _fTitle?.Dispose(); _fHint?.Dispose();
        _fPreview?.Dispose(); _fMeta?.Dispose(); _fBadge?.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Lista disegnata interamente da noi, al posto di un ListBox.
    ///
    /// Il motivo e' lo sfarfallio: il ListBox e' un controllo Win32 e le sue righe
    /// non passano dal doppio buffering gestito. Il Graphics che arriva a DrawItem
    /// e' costruito sull'HDC dello schermo, quindi il disegno va diretto a video
    /// dopo che il controllo ha gia' cancellato la superficie — due passate, un
    /// lampeggio, e nessuna impostazione di buffering lo evita. Sopprimere
    /// WM_ERASEBKGND riduceva il problema senza risolverlo.
    ///
    /// Qui invece UserPaint fa gestire WM_PAINT a WinForms, che con
    /// OptimizedDoubleBuffer dipinge su un back buffer vero e lo riversa in una
    /// passata sola; Opaque evita del tutto la richiesta di cancellazione. Si
    /// ridisegnano solo le righe che intersecano l'area invalidata.
    /// </summary>
    private sealed class ClipList : Control
    {
        private const int ScrollbarW = 4;

        private readonly List<HistoryItem> _items = new();
        private int _selected = -1;
        private int _hover = -1;
        private int _scroll;

        /// <summary>Disegna una riga: superficie, elemento, rettangolo, selezionata, sotto il puntatore.</summary>
        public event Action<Graphics, HistoryItem, Rectangle, bool, bool>? DrawRow;

        public ClipList()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.Opaque
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            TabStop = true;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int ItemHeight { get; set; } = 60;

        public IReadOnlyList<HistoryItem> Items => _items;

        public HistoryItem? SelectedItem =>
            _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                var v = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
                if (v == _selected) return;
                _selected = v;
                EnsureVisible(v);
                Invalidate();
            }
        }

        public void SetItems(IEnumerable<HistoryItem> items)
        {
            var keep = SelectedItem?.Id;
            _items.Clear();
            _items.AddRange(items);
            _selected = keep == null ? -1 : _items.FindIndex(i => i.Id == keep);
            ClampScroll();
            Invalidate();
        }

        /// <summary>Rettangolo della riga in coordinate client (puo' cadere fuori dalla vista).</summary>
        public Rectangle GetItemRectangle(int index) =>
            new(0, index * ItemHeight - _scroll, ClientSize.Width, ItemHeight);

        public int IndexFromPoint(Point p)
        {
            if (ItemHeight <= 0) return -1;
            var i = (p.Y + _scroll) / ItemHeight;
            return i >= 0 && i < _items.Count ? i : -1;
        }

        private int MaxScroll => Math.Max(0, _items.Count * ItemHeight - ClientSize.Height);

        private void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, MaxScroll);

        private void EnsureVisible(int index)
        {
            if (index < 0) return;
            var top = index * ItemHeight;
            if (top < _scroll) _scroll = top;
            else if (top + ItemHeight > _scroll + ClientSize.Height)
                _scroll = top + ItemHeight - ClientSize.Height;
            ClampScroll();
        }

        // Il riscontro al passaggio del mouse conta in un pannello che si usa per
        // scegliere al volo: si ridisegnano solo le due righe coinvolte.
        protected override void OnMouseMove(MouseEventArgs e)
        {
            SetHover(IndexFromPoint(e.Location));
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            SetHover(-1);
            base.OnMouseLeave(e);
        }

        private void SetHover(int index)
        {
            if (index == _hover) return;
            var previous = _hover;
            _hover = index;
            if (previous >= 0) Invalidate(GetItemRectangle(previous));
            if (_hover >= 0) Invalidate(GetItemRectangle(_hover));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            SetHover(-1);   // dopo lo scorrimento la riga sotto il puntatore e' un'altra
            var before = _scroll;
            _scroll -= e.Delta / 120 * ItemHeight;
            ClampScroll();
            if (_scroll != before) Invalidate();
            base.OnMouseWheel(e);
        }

        // Senza questo le frecce e Invio finirebbero alla finestra e non al controllo.
        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown
                    or Keys.Home or Keys.End or Keys.Enter
            || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var page = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight));
            switch (e.KeyCode)
            {
                case Keys.Up: SelectedIndex = Math.Max(0, _selected - 1); e.Handled = true; break;
                case Keys.Down: SelectedIndex = _selected + 1; e.Handled = true; break;
                case Keys.PageUp: SelectedIndex = Math.Max(0, _selected - page); e.Handled = true; break;
                case Keys.PageDown: SelectedIndex = _selected + page; e.Handled = true; break;
                case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
                case Keys.End: SelectedIndex = _items.Count - 1; e.Handled = true; break;
            }
            base.OnKeyDown(e); // la finestra gestisce Invio, Esc e Canc
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, e.ClipRectangle);

            if (_items.Count == 0 || ItemHeight <= 0) return;

            var first = Math.Max(0, (_scroll + e.ClipRectangle.Top) / ItemHeight);
            var last = Math.Min(_items.Count - 1, (_scroll + e.ClipRectangle.Bottom) / ItemHeight);
            for (var i = first; i <= last; i++)
                DrawRow?.Invoke(g, _items[i], GetItemRectangle(i), i == _selected, i == _hover);

            DrawScrollbar(g);
        }

        /// <summary>Barra sottile, disegnata solo quando il contenuto eccede la vista.</summary>
        private void DrawScrollbar(Graphics g)
        {
            var max = MaxScroll;
            if (max <= 0) return;

            var track = ClientSize.Height;
            var h = Math.Max(24, (int)((long)track * track / (_items.Count * ItemHeight)));
            var y = (int)((long)(track - h) * _scroll / max);
            var bar = new Rectangle(ClientSize.Width - ScrollbarW - 2, y, ScrollbarW, h);

            var saved = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(90, Theme.TextMuted)))
                g.FillRoundedRect(b, bar, ScrollbarW / 2);
            g.SmoothingMode = saved;
        }
    }
}

internal static class GraphicsRoundedExtensions
{
    public static void FillRoundedRect(this Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(r, radius);
        g.FillPath(brush, path);
    }

    public static void FillRoundedRect(this Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(r, radius);
        g.FillPath(brush, path);
    }

    public static void AddRoundedRectangle(this GraphicsPath path, RectangleF r, float radius)
    {
        var d = Math.Max(0.5f, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }

    public static void AddRoundedRectangle(this GraphicsPath path, Rectangle r, int radius)
    {
        var d = Math.Max(1, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }
}
