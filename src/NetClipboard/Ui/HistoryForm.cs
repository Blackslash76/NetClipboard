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
    private static readonly Color Bg = Color.FromArgb(24, 24, 30);
    private static readonly Color Card = Color.FromArgb(32, 32, 40);
    private static readonly Color TextMain = Color.FromArgb(238, 238, 244);
    private static readonly Color TextMuted = Color.FromArgb(150, 152, 165);
    private static readonly Color TextSpent = Color.FromArgb(104, 106, 118);
    private static readonly Color AccentA = Color.FromArgb(120, 92, 245);
    private static readonly Color AccentB = Color.FromArgb(56, 180, 220);
    private static readonly Color SelBg = Color.FromArgb(52, 50, 74);

    // Misure logiche (a 96 DPI); vengono scalate da _scale.
    private const int HeaderH = 62;
    private const int FooterH = 28;
    private const int Radius = 16;
    private const int RowH = 62;

    private readonly ClipboardHistory _history;
    private readonly BufferedListBox _list;
    private readonly Dictionary<string, Image> _thumbCache = new();

    /// <summary>Righe gia' ridisegnate come scadute: si smorzano una volta sola.</summary>
    private readonly HashSet<string> _expired = new();

    /// <summary>
    /// Fa scorrere le torte di scadenza mentre la finestra e' aperta, e toglie le
    /// voci esterne appena scadono invece di lasciarle li' fino alla riapertura.
    /// Gira solo a finestra visibile e solo se c'e' qualcosa di esterno.
    /// </summary>
    private readonly System.Windows.Forms.Timer _expiryTick = new() { Interval = 1000 };

    private float _scale = 1f;
    private Font _fTitle = null!, _fSub = null!, _fHint = null!, _fFooter = null!,
                 _fPreview = null!, _fMeta = null!, _fBadge = null!;

    private IntPtr _target;
    /// <summary>Finestra che aveva il fuoco quando si è aperto il popup (per incollarci).</summary>
    public IntPtr TargetWindow => _target;

    public event Action<HistoryItem>? ItemChosen;

    public HistoryForm(ClipboardHistory history)
    {
        _history = history;

        AutoScaleMode = AutoScaleMode.None; // scaliamo noi, manualmente
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Bg;

        _list = new BufferedListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            ForeColor = TextMain,
            IntegralHeight = false,
        };
        _list.DrawItem += OnDrawItem;
        _list.KeyDown += OnKeyDown;
        _list.MouseUp += OnMouseUp;
        Controls.Add(_list);

        Deactivate += (_, _) => Hide();
        Paint += OnPaintChrome;

        _expiryTick.Tick += (_, _) =>
        {
            if (!Visible) { _expiryTick.Stop(); return; }

            // Si tocca SOLO cio' che cambia: le righe scadute si tolgono in posto e
            // delle altre si ridisegna il singolo rettangolo. Ricostruire la lista o
            // invalidarla tutta ogni secondo faceva sfarfallare l'elenco e perdere
            // selezione e posizione di scorrimento.
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

            // Togliere una voce sposta le altre: li' serve una pittura normale,
            // con la cancellazione al suo posto. Solo l'aggiornamento del solo
            // anello puo' saltarla, ed e' quello che lampeggiava.
            if (expired) _list.Invalidate();   // pittura normale: cambia l'aspetto di una riga

            if (!expired && rows.Count > 0)
            {
                _list.SuppressErase = true;
                try
                {
                    foreach (var r in rows) _list.Invalidate(r);
                    _list.Update();   // dipinge subito, finche' la soppressione vale
                }
                finally { _list.SuppressErase = false; }
            }

            if (!alive) _expiryTick.Stop();
        };
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
        _fTitle = PxFont("Segoe UI Semibold", 15.5f);
        _fSub = PxFont("Segoe UI", 11f);
        _fHint = PxFont("Segoe UI", 10.5f);
        _fFooter = PxFont("Segoe UI", 10f, FontStyle.Italic);
        _fPreview = PxFont("Segoe UI", 13f);
        _fMeta = PxFont("Segoe UI", 10.5f);
        _fBadge = PxFont("Segoe UI", 16.5f, FontStyle.Bold);

        ApplyLayout();
    }

    private Font PxFont(string family, float px, FontStyle style = FontStyle.Regular) =>
        new(family, px * _scale, style, GraphicsUnit.Pixel);

    private void ApplyLayout()
    {
        Size = new Size(P(440), P(520));
        _list.ItemHeight = P(RowH);
        _list.SetBounds(P(8), P(HeaderH), Width - P(16), Height - P(HeaderH) - P(FooterH));
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
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var it in _history.Items
                     .OrderByDescending(i => i.Pinned)
                     .ThenByDescending(i => i.TimestampUtc))
            _list.Items.Add(it);
        _list.EndUpdate();
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

        var headerRect = new Rectangle(0, 0, Width, P(HeaderH));
        using (var grad = new LinearGradientBrush(headerRect, AccentA, AccentB, LinearGradientMode.Horizontal))
            g.FillRectangle(grad, headerRect);

        using (var chip = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            g.FillRoundedRect(chip, new Rectangle(P(16), P(16), P(26), P(28)), P(6));
        using (var clipTop = new SolidBrush(AccentA))
            g.FillRoundedRect(clipTop, new Rectangle(P(24), P(12), P(10), P(8)), P(3));

        TextRenderer.DrawText(g, L.T("app.name"), _fTitle, new Point(P(54), P(11)), Color.White, Color.Transparent);
        TextRenderer.DrawText(g, L.T("history.subtitle"), _fSub,
            new Point(P(55), P(34)), Color.FromArgb(232, 242, 247), Color.Transparent);
        TextRenderer.DrawText(g, L.T("history.hint"), _fHint,
            new Rectangle(Width - P(200), P(21), P(190), P(20)), Color.FromArgb(235, 245, 250),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var footRect = new Rectangle(0, Height - P(FooterH), Width, P(FooterH));
        using (var fb = new SolidBrush(Color.FromArgb(18, 18, 22)))
            g.FillRectangle(fb, footRect);
        using (var line = new Pen(Color.FromArgb(45, 45, 55)))
            g.DrawLine(line, 0, footRect.Top, Width, footRect.Top);
        TextRenderer.DrawText(g, L.T("history.credit"), _fFooter, footRect,
            Color.FromArgb(115, 117, 130), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not HistoryItem item) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(Bg))
            g.FillRectangle(bg, e.Bounds);

        var row = new Rectangle(e.Bounds.Left + P(6), e.Bounds.Top + P(4), e.Bounds.Width - P(12), e.Bounds.Height - P(8));
        using (var rb = new SolidBrush(selected ? SelBg : Card))
            g.FillRoundedRect(rb, row, P(10));
        if (selected)
            using (var accent = new SolidBrush(AccentA))
                g.FillRoundedRect(accent, new Rectangle(row.Left, row.Top + P(6), P(3), row.Height - P(12)), P(2));

        var iconSz = P(40);
        var slot = new Rectangle(row.Left + P(12), row.Top + (row.Height - iconSz) / 2, iconSz, iconSz);

        // Sugli esterni l'avatar si stringe per lasciare posto all'anello di
        // scadenza che lo circonda: l'informazione sta addosso al contenuto,
        // invece che in un angolo staccato della riga.
        var icon = item.FromExternal ? Rectangle.Inflate(slot, -P(5), -P(5)) : slot;
        var spent = ClipboardHistory.IsSpent(item);
        var thumb = GetThumb(item, icon.Width);
        if (thumb != null)
        {
            using var clip = new GraphicsPath();
            clip.AddRoundedRectangle(icon, P(8));
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
                g.FillRoundedRect(grad, icon, P(8));
            TextRenderer.DrawText(g, glyph, _fBadge, icon, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Anello solo finche' il conto alla rovescia ha senso: una riga spenta
        // porta l'etichetta, non un anello vuoto.
        if (item.FromExternal && !spent)
            DrawExpiryRing(g, slot, RemainingFraction(item));

        // L'avatar di una riga spenta si smorza, cosi' la riga si legge come
        // disattivata gia' dalla coda dell'occhio, prima di leggere l'etichetta.
        if (spent)
            using (var veil = new SolidBrush(Color.FromArgb(150, Card)))
                g.FillRectangle(veil, slot);

        var textLeft = slot.Right + P(12);
        var textWidth = row.Right - textLeft - P(12);

        // Etichetta di stato a destra: la riga resta visibile come traccia di cio'
        // che e' passato, ma si vede che non e' piu' utilizzabile.
        if (spent)
        {
            var tag = L.T(item.Used ? "history.used" : "history.expired");
            var tagW = TextRenderer.MeasureText(tag, _fMeta).Width + P(4);
            TextRenderer.DrawText(g, tag, _fMeta,
                new Rectangle(row.Right - P(12) - tagW, row.Top + P(9), tagW, P(22)), TextSpent,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
            textWidth -= tagW + P(8);
        }

        TextRenderer.DrawText(g, item.Preview, _fPreview,
            new Rectangle(textLeft, row.Top + P(9), textWidth, P(22)), spent ? TextSpent : TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPadding);

        var pin = item.Pinned ? "📌 " : "";
        var toFetch = item.Kind == PayloadKind.Files && !item.IsLocalOffer
            && (item.LocalRootPaths == null || item.LocalRootPaths.Count == 0) ? L.T("history.toDownload") : "";
        var origin = item.IsLocal ? L.T("history.thisPc") : item.Origin;
        if (item.FromExternal) origin = L.T("history.external", origin);
        var meta = L.T("history.meta", pin, origin, LocalTime(item.TimestampUtc), toFetch);
        TextRenderer.DrawText(g, meta, _fMeta,
            new Rectangle(textLeft, row.Top + P(33), textWidth, P(20)), spent ? TextSpent : TextMuted,
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
        var color = fraction <= 0.25
            ? Color.FromArgb(240, 150, 60)
            : Color.FromArgb(110, 170, 240);

        var thickness = Math.Max(2f, box.Width / 14f);
        var arc = Rectangle.Inflate(box, (int)(-thickness / 2), (int)(-thickness / 2));

        var saved = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var track = new Pen(Color.FromArgb(60, 130, 135, 155), thickness))
            g.DrawEllipse(track, arc);

        if (fraction > 0)
            using (var pen = new Pen(color, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, arc, -90f, (float)(360.0 * fraction));

        g.SmoothingMode = saved;
    }

    private static (Color, Color, string) BadgeStyle(HistoryItem item) => item.Kind switch
    {
        PayloadKind.Text => (Color.FromArgb(70, 120, 235), Color.FromArgb(60, 90, 200), "T"),
        PayloadKind.Files => (Color.FromArgb(240, 170, 60), Color.FromArgb(220, 130, 40), item.DirCount > 0 ? "🗀" : "🗎"),
        _ => (Color.FromArgb(60, 170, 120), Color.FromArgb(40, 140, 100), "?"),
    };

    private Image? GetThumb(HistoryItem item, int size)
    {
        if (item.Kind != PayloadKind.Image || item.BlobFile == null) return null;
        var key = item.Id + "@" + size;
        if (_thumbCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var path = Path.Combine(AppConfig.AppDataDir, "history", item.BlobFile);
            if (!File.Exists(path)) return null;
            using var src = Image.FromFile(path);
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
        _fTitle?.Dispose(); _fSub?.Dispose(); _fHint?.Dispose(); _fFooter?.Dispose();
        _fPreview?.Dispose(); _fMeta?.Dispose(); _fBadge?.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    private sealed class BufferedListBox : ListBox
    {
        private const int WM_ERASEBKGND = 0x0014;

        /// <summary>
        /// Mentre e' true la cancellazione dello sfondo viene saltata.
        ///
        /// Il ListBox e' un controllo Win32: prima cancella la superficie, poi
        /// chiede il disegno della riga. Due passate sullo schermo, cioe' un
        /// lampeggio, che il doppio buffering di WinForms non copre perche' il
        /// disegno non passa dal back buffer gestito. Si tiene attiva solo per il
        /// ridisegno periodico dell'anello: le pitture normali cancellano come
        /// sempre, cosi' l'area sotto l'ultima riga resta pulita.
        /// </summary>
        public bool SuppressErase;

        public BufferedListBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (SuppressErase && m.Msg == WM_ERASEBKGND) { m.Result = 1; return; }
            base.WndProc(ref m);
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
