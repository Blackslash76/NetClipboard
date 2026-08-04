using System.Drawing.Drawing2D;
using System.IO;
using NetClipboard.Core;

namespace NetClipboard.Ui;

/// <summary>
/// Popup della cronologia condivisa (il "nostro Win+V" cross-device), con una
/// grafica curata: angoli arrotondati, ombra, header con gradiente e badge
/// colorati per tipo. Si apre con Win+Alt+V vicino al cursore.
/// </summary>
public sealed class HistoryForm : Form
{
    // Palette
    private static readonly Color Bg = Color.FromArgb(24, 24, 30);
    private static readonly Color Card = Color.FromArgb(32, 32, 40);
    private static readonly Color TextMain = Color.FromArgb(238, 238, 244);
    private static readonly Color TextMuted = Color.FromArgb(150, 152, 165);
    private static readonly Color AccentA = Color.FromArgb(120, 92, 245); // viola
    private static readonly Color AccentB = Color.FromArgb(56, 180, 220);  // ciano
    private static readonly Color SelBg = Color.FromArgb(52, 50, 74);

    private const int HeaderH = 60;
    private const int FooterH = 26;
    private const int Radius = 16;

    private readonly ClipboardHistory _history;
    private readonly BufferedListBox _list;
    private readonly Dictionary<string, Image> _thumbCache = new();

    public event Action<HistoryItem>? ItemChosen;

    public HistoryForm(ClipboardHistory history)
    {
        _history = history;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(440, 520);
        BackColor = Bg;

        _list = new BufferedListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 60,
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            ForeColor = TextMain,
            IntegralHeight = false,
        };
        _list.SetBounds(8, HeaderH, Width - 16, Height - HeaderH - FooterH);
        _list.DrawItem += OnDrawItem;
        _list.DoubleClick += (_, _) => ChooseSelected();
        _list.KeyDown += OnKeyDown;
        _list.MouseUp += OnMouseUp;
        Controls.Add(_list);

        Deactivate += (_, _) => Hide();
        Paint += OnPaintChrome;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW: ombra leggera
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), Radius);
        Region = new Region(path);
    }

    public void ShowNearCursor()
    {
        _history.PurgeExpired();
        Reload();
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
        if (_list.SelectedItem is HistoryItem item)
        {
            Hide();
            ItemChosen?.Invoke(item);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                ChooseSelected();
                e.Handled = true;
                break;
            case Keys.Escape:
                Hide();
                e.Handled = true;
                break;
            case Keys.Delete:
                if (_list.SelectedItem is HistoryItem del)
                {
                    _history.Remove(del.Id);
                    Reload();
                }
                e.Handled = true;
                break;
        }
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;
        var idx = _list.IndexFromPoint(e.Location);
        if (idx < 0)
            return;
        _list.SelectedIndex = idx;
        if (_list.SelectedItem is not HistoryItem item)
            return;

        var menu = new ContextMenuStrip();
        menu.Items.Add(item.Pinned ? "Rimuovi pin" : "Aggiungi pin", null, (_, _) =>
        {
            _history.TogglePin(item.Id);
            Reload();
        });
        menu.Items.Add("Elimina", null, (_, _) =>
        {
            _history.Remove(item.Id);
            Reload();
        });
        menu.Show(_list, e.Location);
    }

    // ----- Chrome: header con gradiente + footer con firma -----

    private void OnPaintChrome(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Sfondo header con gradiente
        var headerRect = new Rectangle(0, 0, Width, HeaderH);
        using (var grad = new LinearGradientBrush(headerRect, AccentA, AccentB, LinearGradientMode.Horizontal))
            g.FillRectangle(grad, headerRect);

        // Piccolo "logo" clipboard
        using (var chip = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
        {
            g.FillRoundedRect(chip, new Rectangle(16, 16, 26, 28), 6);
        }
        using (var clipTop = new SolidBrush(AccentA))
            g.FillRoundedRect(clipTop, new Rectangle(24, 12, 10, 8), 3);

        TextRenderer.DrawText(g, "NetClipboard", new Font("Segoe UI Semibold", 12f),
            new Point(54, 12), Color.White, Color.Transparent);
        TextRenderer.DrawText(g, "Appunti condivisi tra i tuoi dispositivi", new Font("Segoe UI", 8.25f),
            new Point(55, 34), Color.FromArgb(230, 240, 245), Color.Transparent);

        // Hint a destra
        TextRenderer.DrawText(g, "Invio incolla · Esc chiude", new Font("Segoe UI", 8f),
            new Rectangle(Width - 210, 22, 194, 18), Color.FromArgb(235, 245, 250),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        // Footer + firma (discreta ma sempre presente)
        var footRect = new Rectangle(0, Height - FooterH, Width, FooterH);
        using (var fb = new SolidBrush(Color.FromArgb(18, 18, 22)))
            g.FillRectangle(fb, footRect);
        using (var line = new Pen(Color.FromArgb(45, 45, 55)))
            g.DrawLine(line, 0, footRect.Top, Width, footRect.Top);
        TextRenderer.DrawText(g, "creato da Francesco Papeo",
            new Font("Segoe UI", 7.75f, FontStyle.Italic),
            footRect, Color.FromArgb(110, 112, 125),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // ----- Disegno voci -----

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not HistoryItem item)
            return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var selected = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(Bg))
            g.FillRectangle(bg, e.Bounds);

        // Riga con angoli arrotondati
        var row = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 4, e.Bounds.Width - 12, e.Bounds.Height - 8);
        using (var rb = new SolidBrush(selected ? SelBg : Card))
            g.FillRoundedRect(rb, row, 10);
        if (selected)
            using (var accent = new SolidBrush(AccentA))
                g.FillRoundedRect(accent, new Rectangle(row.Left, row.Top + 6, 3, row.Height - 12), 2);

        // Icona / thumbnail (40x40)
        var icon = new Rectangle(row.Left + 12, row.Top + (row.Height - 40) / 2, 40, 40);
        var thumb = GetThumb(item);
        if (thumb != null)
        {
            using var clip = new GraphicsPath();
            clip.AddRoundedRectangle(icon, 8);
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
                g.FillRoundedRect(grad, icon, 8);
            TextRenderer.DrawText(g, glyph, new Font("Segoe UI", 13f, FontStyle.Bold),
                icon, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var textLeft = icon.Right + 12;
        var textWidth = row.Right - textLeft - 12;

        TextRenderer.DrawText(g, item.Preview, new Font("Segoe UI", 9.75f),
            new Rectangle(textLeft, row.Top + 9, textWidth, 20), TextMain,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left);

        var pin = item.Pinned ? "📌 " : "";
        var toFetch = item.Kind == PayloadKind.Files && !item.IsLocalOffer
            && (item.LocalRootPaths == null || item.LocalRootPaths.Count == 0)
            ? "  ·  da scaricare" : "";
        var meta = $"{pin}{(item.IsLocal ? "questo PC" : item.Origin)}  ·  {LocalTime(item.TimestampUtc)}{toFetch}";
        TextRenderer.DrawText(g, meta, new Font("Segoe UI", 8f),
            new Rectangle(textLeft, row.Top + 31, textWidth, 18), TextMuted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
    }

    private static (Color, Color, string) BadgeStyle(HistoryItem item) => item.Kind switch
    {
        PayloadKind.Text => (Color.FromArgb(70, 120, 235), Color.FromArgb(60, 90, 200), "T"),
        PayloadKind.Files => item.DirCount > 0
            ? (Color.FromArgb(240, 170, 60), Color.FromArgb(220, 130, 40), "🗀")
            : (Color.FromArgb(240, 170, 60), Color.FromArgb(220, 130, 40), "🗎"),
        _ => (Color.FromArgb(60, 170, 120), Color.FromArgb(40, 140, 100), "?"),
    };

    private Image? GetThumb(HistoryItem item)
    {
        if (item.Kind != PayloadKind.Image || item.BlobFile == null)
            return null;
        if (_thumbCache.TryGetValue(item.Id, out var cached))
            return cached;
        try
        {
            var path = Path.Combine(AppConfig.AppDataDir, "history", item.BlobFile);
            if (!File.Exists(path))
                return null;
            using var src = Image.FromFile(path);
            var thumb = new Bitmap(40, 40);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, 40, 40);
            }
            _thumbCache[item.Id] = thumb;
            return thumb;
        }
        catch
        {
            return null;
        }
    }

    private static string LocalTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTime.Now - local;
        if (delta.TotalSeconds < 60) return "adesso";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min fa";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} h fa";
        return local.ToString("dd/MM HH:mm");
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    /// <summary>ListBox con doppio buffer per un disegno fluido e senza sfarfallio.</summary>
    private sealed class BufferedListBox : ListBox
    {
        public BufferedListBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
        }
    }
}

/// <summary>Helper di disegno arrotondato riusabili.</summary>
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
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }
}
