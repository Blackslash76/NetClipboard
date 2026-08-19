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
    private readonly Label _code;
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
        _code = new Label
        {
            Text = string.Join("  ", prompt.Sas.ToCharArray()),
            TextAlign = ContentAlignment.MiddleCenter,
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

        var full = ClientW - 2 * Pad;
        var y = 18;

        // il codice deve starci TUTTO: riduce il corpo finché non entra nella riga
        var px = 40f;
        while (px > 20f)
        {
            using var probe = new Font("Consolas", px * ScaleFactor, FontStyle.Bold, GraphicsUnit.Pixel);
            if (TextRenderer.MeasureText(_code.Text, probe).Width <= P(full - 8))
                break;
            px -= 2f;
        }
        _code.Font = PxFont("Consolas", px, FontStyle.Bold);

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
}
