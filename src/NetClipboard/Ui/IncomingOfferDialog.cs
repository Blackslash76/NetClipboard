using NetClipboard.Core;
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
    private readonly Label _warn = new()
    {
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Silver,
    };
    private readonly Button _accept = new() { DialogResult = DialogResult.OK };
    private readonly Button _refuse = new() { DialogResult = DialogResult.Cancel };

    public IncomingOfferDialog(IncomingOffer offer)
    {
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = Color.FromArgb(28, 28, 34);
        ForeColor = Color.White;

        Text = L.T("incoming.title");
        _title.Text = L.T("incoming.heading");
        _warn.Text = L.T("incoming.warning");
        _accept.Text = L.T("incoming.accept");
        _refuse.Text = L.T("common.cancel");

        _from = new Label
        {
            Text = L.T("incoming.fromLine", offer.FromLabel, KindLabel(offer.Kind)),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gainsboro,
        };
        _preview = new Label
        {
            Text = offer.Preview,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(120, 200, 255),
            AutoEllipsis = true,
        };

        foreach (var b in new[] { _accept, _refuse })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(55, 55, 66);
        }
        _accept.BackColor = Color.FromArgb(30, 120, 200);
        AcceptButton = _accept;
        CancelButton = _refuse;

        Controls.AddRange(new Control[] { _title, _from, _preview, _warn, _accept, _refuse });

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

        var full = ClientW - 2 * Pad;
        var y = 18;

        _title.SetBounds(P(Pad), P(y), P(full), P(26)); y += 34;
        _from.SetBounds(P(Pad), P(y), P(full), P(42)); y += 48;
        _preview.SetBounds(P(Pad), P(y), P(full), P(44)); y += 52;
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
