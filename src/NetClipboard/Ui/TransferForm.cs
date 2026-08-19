using System.Drawing.Drawing2D;
using NetClipboard.Core;

namespace NetClipboard.Ui;

/// <summary>
/// Finestrella di avanzamento per il trasferimento dei file (stile "copia di
/// Esplora risorse", ma con la grafica dell'app): barra, velocità, tempo residuo,
/// nome dell'elemento in corso e pulsante Annulla.
/// Non ruba MAI il fuoco (WS_EX_NOACTIVATE), altrimenti l'incolla nella finestra
/// di destinazione fallirebbe.
/// </summary>
public sealed class TransferForm : ScaledForm
{
    // Misure logiche (a 96 DPI).
    private const int ClientW = 470;
    private const int Pad = 20;
    private const int BarH = 10;

    private readonly string _title;
    private readonly string _subtitle;
    private readonly long _totalBytes;
    private readonly CancellationTokenSource _cts;

    private readonly Button _cancelBtn = new();
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _delayedShow = new() { Interval = 400 };

    private Font _fTitle = null!, _fSub = null!, _fStats = null!, _fName = null!;

    private long _done;
    private string _current = "";
    private bool _cancelling;
    private bool _closed;

    // Velocità: media mobile su campioni da 250 ms.
    private long _lastBytes;
    private long _lastTick;
    private double _speed;
    private int _marquee;

    public TransferForm(string title, string subtitle, long totalBytes, CancellationTokenSource cts)
    {
        _title = title;
        _subtitle = subtitle;
        _totalBytes = totalBytes;
        _cts = cts;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        DoubleBuffered = true;

        _cancelBtn.Text = L.T("common.cancel");
        _cancelBtn.FlatStyle = FlatStyle.Flat;
        _cancelBtn.FlatAppearance.BorderSize = 1;
        _cancelBtn.Click += (_, _) => CancelTransfer();
        Controls.Add(_cancelBtn);

        _clock.Tick += (_, _) => OnClock();
        _delayedShow.Tick += (_, _) =>
        {
            _delayedShow.Stop();
            if (!_closed) { PlaceBottomRight(); Show(); }
        };

        _lastTick = Environment.TickCount64;
        Theme.Attach(this, ApplyTheme);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.TextMain;
        _cancelBtn.BackColor = Theme.ButtonFace;
        _cancelBtn.ForeColor = Theme.ButtonText;
        _cancelBtn.FlatAppearance.BorderColor = Theme.Divider;
    }

    // Non attivare mai la finestra: il fuoco deve restare all'app di destinazione,
    // altrimenti il Ctrl+V finale andrebbe a finire nel posto sbagliato.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x08000000;    // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW: fuori da Alt+Tab
            // WS_EX_TOPMOST come stile di CREAZIONE: la proprietà Form.TopMost
            // attiverebbe la finestra rubando il fuoco (verificato), questo no.
            cp.ExStyle |= 0x00000008;
            return cp;
        }
    }

    protected override void ApplyLayout()
    {
        _fTitle = PxFont("Segoe UI Semibold", 15f);
        _fSub = PxFont("Segoe UI", 12f);
        _fStats = PxFont("Segoe UI", 12f);
        _fName = PxFont("Segoe UI", 11.5f);

        ClientSize = new Size(P(ClientW), P(178));
        _cancelBtn.SetBounds(P(ClientW - Pad - 100), P(178 - Pad - 30), P(100), P(30));

        using var path = new GraphicsPath();
        path.AddRoundedRectangle(new Rectangle(0, 0, Width, Height), P(14));
        Region = new Region(path);
    }

    /// <summary>Mostra la finestra solo se il trasferimento dura abbastanza da giustificarla.</summary>
    public void ShowAfterDelay()
    {
        _ = Handle; // handle subito: i report arrivano da un altro thread
        _clock.Start();
        _delayedShow.Start();
    }

    /// <summary>Aggiorna l'avanzamento (da chiamare sul thread della UI).</summary>
    public void Report(string currentName, long bytesDone)
    {
        _done = bytesDone;
        if (!string.IsNullOrEmpty(currentName))
            _current = currentName;
        if (Visible) Invalidate();
    }

    /// <summary>Chiude la finestra a trasferimento concluso (o fallito).</summary>
    public void Finish()
    {
        _closed = true;
        _delayedShow.Stop();
        _clock.Stop();
        Hide();
        Dispose();
    }

    private void CancelTransfer()
    {
        if (_cancelling) return;
        _cancelling = true;
        _cancelBtn.Enabled = false;
        try { _cts.Cancel(); } catch { }
        Invalidate();
    }

    private void OnClock()
    {
        var now = Environment.TickCount64;
        var dt = (now - _lastTick) / 1000.0;
        if (dt >= 0.2)
        {
            var inst = (_done - _lastBytes) / dt;
            _speed = _speed <= 0 ? inst : _speed * 0.7 + inst * 0.3;
            _lastBytes = _done;
            _lastTick = now;
        }
        _marquee = (_marquee + 6) % 100;
        if (Visible) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var left = P(Pad);
        var w = Width - P(2 * Pad);
        var y = P(Pad);

        TextRenderer.DrawText(g, _title, _fTitle, new Point(left, y), Theme.TextMain, Color.Transparent);
        y += P(24);
        TextRenderer.DrawText(g, _subtitle, _fSub, new Point(left, y), Theme.TextMuted, Color.Transparent);
        y += P(26);

        DrawBar(g, new Rectangle(left, y, w, P(BarH)));
        y += P(BarH + 12);

        TextRenderer.DrawText(g, StatsLine(), _fStats, new Rectangle(left, y, w, P(18)), Theme.TextMain,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        y += P(22);

        var name = _cancelling ? L.T("transfer.cancelling") : _current;
        TextRenderer.DrawText(g, name, _fName, new Rectangle(left, y, w, P(18)), Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawBar(Graphics g, Rectangle r)
    {
        var radius = r.Height / 2;
        using (var tb = new SolidBrush(Theme.Track))
            g.FillRoundedRect(tb, r, radius);

        Rectangle fill;
        if (_totalBytes > 0)
        {
            var ratio = Math.Clamp(_done / (double)_totalBytes, 0, 1);
            var fw = (int)Math.Round(r.Width * ratio);
            if (fw < r.Height && fw > 0) fw = r.Height; // resta visibile anche all'inizio
            fill = new Rectangle(r.X, r.Y, fw, r.Height);
        }
        else
        {
            // dimensione totale ignota: blocchetto che scorre
            var bw = r.Width / 4;
            fill = new Rectangle(r.X + (int)(r.Width * (_marquee / 100.0)) - bw / 2, r.Y, bw, r.Height);
            fill.Intersect(r);
        }

        if (fill.Width <= 0) return;
        using var grad = new LinearGradientBrush(r, Theme.Accent, Theme.AccentAlt, LinearGradientMode.Horizontal);
        var clip = g.Clip;
        g.SetClip(fill, CombineMode.Intersect);
        g.FillRoundedRect(grad, r, radius);
        g.Clip = clip;
    }

    private string StatsLine()
    {
        var done = ClipboardPayload.HumanSize(_done);
        var speed = _speed > 1
            ? L.T("transfer.perSecond", ClipboardPayload.HumanSize((long)_speed))
            : L.T("transfer.speedUnknown");
        if (_totalBytes <= 0)
            return L.T("transfer.statsNoTotal", done, speed);

        var pct = (int)Math.Round(Math.Clamp(_done * 100.0 / _totalBytes, 0, 100));
        return L.T("transfer.stats", pct, done, ClipboardPayload.HumanSize(_totalBytes), speed, Remaining());
    }

    private string Remaining()
    {
        if (_speed < 1 || _totalBytes <= 0) return L.T("transfer.calculating");
        var secs = (_totalBytes - _done) / _speed;
        if (secs < 5) return L.T("transfer.almostDone");
        if (secs < 60) return L.T("transfer.secondsLeft", (int)secs);
        if (secs < 3600) return L.T("transfer.minutesLeft", (int)(secs / 60));
        return L.T("transfer.overAnHour");
    }

    private void PlaceBottomRight()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - P(24), wa.Bottom - Height - P(24));
    }

    // Trascinabile dal corpo della finestra (non ha barra del titolo).
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        NativePaste.ReleaseCapture();
        NativePaste.SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, 2 /*HTCAPTION*/, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Dispose();
            _delayedShow.Dispose();
        }
        base.Dispose(disposing);
    }
}
