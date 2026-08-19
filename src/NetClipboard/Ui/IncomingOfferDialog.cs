using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Chiede se accettare un contenuto mandato da un collega non accoppiato.
///
/// A differenza del mirroring fra i propri PC, che è silenzioso, qui il mittente
/// è qualcun altro: niente finisce negli appunti senza un sì esplicito. Si
/// auto-rifiuta dopo 60 secondi, così un invio a una postazione lasciata libera
/// non resta appeso a tempo indefinito. DPI-aware (vedi <see cref="ScaledForm"/>).
/// </summary>
public sealed class IncomingOfferDialog : ScaledForm
{
    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 420;
    private const int Pad = 20;

    private int _secondsLeft = 60;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private readonly Label _title = new();
    private readonly Label _from;
    private readonly Label _preview;
    private readonly Label _warn = new() { TextAlign = ContentAlignment.MiddleCenter };
    /// <summary>
    /// Esito del controllo antivirus. Compare solo quando l'analisi e' avvenuta
    /// davvero su questo PC: un bollino verde non veritiero sarebbe peggio del
    /// silenzio, perche' rassicura senza che nessuno abbia controllato.
    /// </summary>
    private readonly Label _scan = new() { TextAlign = ContentAlignment.MiddleCenter };

    private readonly Button _accept = new() { DialogResult = DialogResult.OK };
    private readonly Button _refuse = new() { DialogResult = DialogResult.Cancel };

    /// <summary>Quale dei tre livelli di verifica si sta mostrando (decide la tinta).</summary>
    private enum ScanTone { None, Clean, Pending, SystemGuard }

    private readonly ScanTone _tone;

    public IncomingOfferDialog(IncomingOffer offer)
    {
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;

        Text = L.T("incoming.title");
        _title.Text = L.T("incoming.heading");
        _warn.Text = L.T("incoming.warning");
        _accept.Text = L.T("incoming.accept");
        _refuse.Text = L.T("common.cancel");

        _from = new Label
        {
            Text = L.T("incoming.fromLine", offer.FromLabel, KindLabel(offer.Kind)),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _preview = new Label
        {
            Text = offer.Preview,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
        };

        // Tre livelli, dal piu' forte al piu' debole, e nessuno di piu' di quanto
        // si sappia davvero:
        //  1. il contenuto e' stato analizzato qui e risulta pulito;
        //  2. non e' ancora arrivato (i file viaggiano dopo), ma lo sara';
        //  3. non possiamo dare un verdetto sul singolo contenuto, pero' Windows
        //     conferma che un antivirus e' attivo e controlla cio' che viene
        //     scritto su disco. E' meno, ma e' vero.
        if (offer.Scan == ScanVerdict.Clean)
        {
            _scan.Text = L.T("incoming.verified");
            _tone = ScanTone.Clean;
        }
        else if (offer.Kind == PayloadKind.Files && AntimalwareScan.Available)
        {
            _scan.Text = L.T("incoming.willVerify");
            _tone = ScanTone.Pending;
        }
        else if (SystemProtection.Antivirus == ProtectionState.Active)
        {
            _scan.Text = L.T("incoming.systemProtected");
            _tone = ScanTone.SystemGuard;
        }
        else
        {
            _tone = ScanTone.None;
            _scan.Visible = false;
        }

        foreach (var b in new[] { _accept, _refuse })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
        }
        AcceptButton = _accept;
        CancelButton = _refuse;

        Controls.AddRange(new Control[] { _title, _from, _preview, _scan, _warn, _accept, _refuse });

        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            _accept.Text = L.T("incoming.acceptCountdown", _secondsLeft);
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
        _from.ForeColor = Theme.TextMain;
        _preview.ForeColor = Theme.Info;
        _scan.ForeColor = _tone switch
        {
            ScanTone.Clean => Theme.Success,
            ScanTone.Pending => Theme.TextMuted,
            _ => Theme.Info,
        };

        _refuse.BackColor = Theme.ButtonFace;
        _refuse.ForeColor = Theme.ButtonText;
        _refuse.FlatAppearance.BorderColor = Theme.Divider;
        _accept.BackColor = Theme.Primary;
        _accept.ForeColor = Theme.OnAccent;
        _accept.FlatAppearance.BorderColor = Theme.Primary;
    }

    private static string KindLabel(PayloadKind kind) => L.T(kind switch
    {
        PayloadKind.Text => "incoming.kindText",
        PayloadKind.Image => "incoming.kindImage",
        _ => "incoming.kindFiles",
    });

    protected override Font CreateBaseFont() => PxFont("Segoe UI", 12.5f);

    protected override void ApplyLayout()
    {
        _title.Font = PxFont("Segoe UI Semibold", 16f);
        _preview.Font = PxFont("Segoe UI", 12f);
        _scan.Font = PxFont("Segoe UI Semibold", 11.5f);

        var full = ClientW - 2 * Pad;
        var y = 18;

        _title.SetBounds(P(Pad), P(y), P(full), P(26)); y += 34;
        _from.SetBounds(P(Pad), P(y), P(full), P(42)); y += 48;
        _preview.SetBounds(P(Pad), P(y), P(full), P(44)); y += 50;
        if (_tone != ScanTone.None) { _scan.SetBounds(P(Pad), P(y), P(full), P(22)); y += 26; }
        _warn.SetBounds(P(Pad), P(y), P(full), P(40)); y += 48;

        _refuse.SetBounds(P(ClientW - Pad - 104), P(y), P(104), P(32));
        _accept.SetBounds(_refuse.Left - P(112), P(y), P(104), P(32));
        y += 32 + Pad;

        ClientSize = new Size(P(ClientW), P(y));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
