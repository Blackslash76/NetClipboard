using NetClipboard.Core;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Chiede all'utente di confermare che il codice a 6 cifre coincide con quello
/// mostrato sull'altro PC (numeric comparison anti-intercettazione). Si auto-annulla
/// dopo 60 secondi. Interamente DPI-aware (vedi <see cref="ScaledForm"/>).
/// </summary>
public sealed class SasDialog : ScaledForm
{
    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 400;
    private const int Pad = 20;

    private int _secondsLeft = 60;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private readonly Label _title = new();
    private readonly Label _peer;
    private readonly CodeLine _code;
    private readonly Label _warn = new() { TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _ok = new() { DialogResult = DialogResult.OK };
    private readonly Button _cancel = new() { DialogResult = DialogResult.Cancel };

    public SasDialog(PairingPrompt prompt)
    {
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;

        Text = L.T("sas.title");
        _title.Text = L.T("sas.heading");
        _warn.Text = L.T("sas.warning");
        _ok.Text = L.T("sas.confirm");
        _cancel.Text = L.T("common.cancel");

        _peer = new Label
        {
            Text = L.T("sas.peerLine", prompt.PeerName, prompt.Fingerprint),
        };
        _code = new CodeLine
        {
            Text = string.Join("  ", prompt.Sas.ToCharArray()),
        };

        foreach (var b in new[] { _ok, _cancel })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
        }
        AcceptButton = _ok;
        CancelButton = _cancel;

        Controls.AddRange(new Control[] { _title, _peer, _code, _warn, _ok, _cancel });

        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            _ok.Text = L.T("sas.confirmCountdown", _secondsLeft);
            if (_secondsLeft <= 0)
            {
                _timer.Stop();
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
        _timer.Start();
        Theme.Attach(this, ApplyTheme);
    }

    private void ApplyTheme()
    {
        BackColor = Theme.Bg;
        ForeColor = Theme.TextMain;
        _warn.ForeColor = Theme.TextMuted;
        _peer.ForeColor = Theme.TextMain;
        _code.ForeColor = Theme.Info;

        _cancel.BackColor = Theme.ButtonFace;
        _cancel.ForeColor = Theme.ButtonText;
        _cancel.FlatAppearance.BorderColor = Theme.Divider;
        _ok.BackColor = Theme.Primary;
        _ok.ForeColor = Theme.OnAccent;
        _ok.FlatAppearance.BorderColor = Theme.Primary;
    }

    protected override Font CreateBaseFont() => PxFont("Segoe UI", 12.5f);

    protected override void ApplyLayout()
    {
        _title.Font = PxFont("Segoe UI Semibold", 16f);
        _cancel.Font = PxFont("Segoe UI", 13f);
        _ok.Font = PxFont("Segoe UI Semibold", 13f);

        var full = ClientW - 2 * Pad;
        var y = 18;

        // Al codice si dicono solo gli estremi: il corpo giusto lo sceglie da sé
        // mentre disegna, dove è l'unico posto in cui la misura non può mentire.
        _code.MaxPx = P(40);
        _code.MinPx = P(18);

        _title.SetBounds(P(Pad), P(y), P(full), P(26)); y += 32;
        _peer.SetBounds(P(Pad), P(y), P(full), P(42)); y += 50;
        _code.SetBounds(P(Pad), P(y), P(full), P(58)); y += 66;
        _warn.SetBounds(P(Pad), P(y), P(full), P(40)); y += 50;

        _cancel.SetBounds(P(ClientW - Pad - 104), P(y), P(104), P(32));
        _ok.SetBounds(_cancel.Left - P(112), P(y), P(104), P(32));
        y += 32 + Pad;

        ClientSize = new Size(P(ClientW), P(y));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// La riga con le sei cifre. Sceglie il corpo del carattere <b>mentre
    /// disegna</b>, misurandolo sul contesto grafico su cui sta disegnando.
    ///
    /// Prima il corpo veniva scelto durante il layout, misurando con
    /// <c>TextRenderer.MeasureText(testo, font)</c> — che misura su un contesto
    /// suo, non su quello del monitor dove la finestra finirà. Con due schermi a
    /// scalatura diversa (qui 150% e 200%) le due cose non coincidono: il testo
    /// veniva misurato per un DPI e disegnato per un altro, più grande di un
    /// terzo, e usciva dalla riga. Chi guardava vedeva <b>quattro cifre su sei</b>
    /// e non aveva modo di accorgersi che ne mancavano due — su un codice che
    /// serve proprio a essere confrontato, è il difetto peggiore possibile.
    /// Trascinare la finestra sull'altro monitor rifaceva il layout e sistemava
    /// tutto, il che rendeva il difetto anche difficile da cogliere.
    ///
    /// Misurare nel momento del disegno toglie il problema alla radice, invece di
    /// compensarlo con un margine indovinato: il contesto su cui si misura è per
    /// costruzione quello su cui si disegna.
    /// </summary>
    private sealed class CodeLine : Control
    {
        // Campi e non proprietà: l'analizzatore WFO1000 rifiuta le proprietà
        // pubbliche non serializzabili su un Control.
        public int MaxPx = 40;
        public int MinPx = 18;

        public CodeLine()
        {
            // Owner-drawn e a doppio buffer: un ridisegno a ogni secondo (c'è un
            // conto alla rovescia accanto) non deve far sfarfallare il codice.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            ResizeRedraw = true;
            TabStop = false;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (string.IsNullOrEmpty(Text))
                return;

            const TextFormatFlags flags = TextFormatFlags.HorizontalCenter |
                                          TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.SingleLine |
                                          TextFormatFlags.NoPadding;

            var box = ClientRectangle;
            for (var px = MaxPx; px > MinPx; px -= 2)
            {
                using var probe = new Font("Consolas", px, FontStyle.Bold, GraphicsUnit.Pixel);
                var size = TextRenderer.MeasureText(e.Graphics, Text, probe);
                if (size.Width <= box.Width && size.Height <= box.Height)
                {
                    TextRenderer.DrawText(e.Graphics, Text, probe, box, ForeColor, flags);
                    return;
                }
            }

            // Nemmeno il corpo minimo ci sta: si disegna comunque piccolo. Un
            // codice minuscolo si legge, un codice tagliato inganna.
            using var smallest = new Font("Consolas", MinPx, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(e.Graphics, Text, smallest, box, ForeColor, flags);
        }
    }
}
