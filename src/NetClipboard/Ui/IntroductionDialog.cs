using NetClipboard.Core;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Un dispositivo gia' fidato ne presenta un altro che non conosciamo.
///
/// Fino alla 2.6.2 la presentazione bastava da sola: il nuovo entrava in silenzio.
/// Ma la fiducia e' dell'utente, non della rete — bastava che UN dispositivo
/// accoppiato finisse in mano a qualcun altro perche' la sua chiave entrasse in
/// tutti gli altri. Qui si dice chi presenta chi, e decide la persona.
///
/// Si auto-chiude dopo 60 secondi SENZA rispondere: nessuno decide al posto
/// dell'utente, e la proposta ritorna piu' tardi. DPI-aware (vedi <see cref="ScaledForm"/>).
/// </summary>
public sealed class IntroductionDialog : ScaledForm
{
    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 430;
    private const int Pad = 20;

    private int _secondsLeft = 60;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private readonly Label _title = new();
    private readonly Label _who;
    private readonly Label _fingerprint;
    private readonly Label _warn = new() { TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _accept = new() { DialogResult = DialogResult.OK };
    private readonly Button _refuse = new() { DialogResult = DialogResult.Cancel };

    /// <summary>
    /// Vero se il tempo e' scaduto senza risposta. Non e' un rifiuto: un rifiuto
    /// resta scritto per sempre, mentre chi non era davanti al PC merita che la
    /// proposta ritorni.
    /// </summary>
    public bool TimedOut { get; private set; }

    public IntroductionDialog(IntroductionPrompt prompt)
    {
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;

        Text = L.T("intro.title");
        _title.Text = L.T("intro.heading");
        _warn.Text = L.T("intro.warning");
        _accept.Text = L.T("intro.accept");
        _refuse.Text = L.T("intro.refuse");

        _who = new Label
        {
            Text = L.T("intro.whoLine", prompt.IntroducerName, prompt.NewDeviceName),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _fingerprint = new Label
        {
            Text = L.T("intro.fingerprint", prompt.Fingerprint),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        foreach (var b in new[] { _accept, _refuse })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
        }
        AcceptButton = _refuse;   // il tasto Invio non deve far entrare nessuno
        CancelButton = _refuse;

        Controls.AddRange(new Control[] { _title, _who, _fingerprint, _warn, _accept, _refuse });

        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            _refuse.Text = L.T("intro.refuseCountdown", _secondsLeft);
            if (_secondsLeft <= 0)
            {
                _timer.Stop();
                TimedOut = true;
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
        _who.ForeColor = Theme.TextMain;
        _fingerprint.ForeColor = Theme.Info;
        _warn.ForeColor = Theme.TextMuted;

        // L'accento sta sul rifiuto, non sull'accettazione: qui la scelta prudente e'
        // dire di no, ed e' anche quella che fanno Invio ed Esc. Un pulsante acceso
        // su "Aggiungilo" spingerebbe nella direzione sbagliata chi legge di fretta.
        _refuse.BackColor = Theme.Primary;
        _refuse.ForeColor = Theme.OnAccent;
        _refuse.FlatAppearance.BorderColor = Theme.Primary;
        _accept.BackColor = Theme.ButtonFace;
        _accept.ForeColor = Theme.ButtonText;
        _accept.FlatAppearance.BorderColor = Theme.Divider;
    }

    protected override Font CreateBaseFont() => PxFont("Segoe UI", 12.5f);

    protected override void ApplyLayout()
    {
        _title.Font = PxFont("Segoe UI Semibold", 16f);
        _fingerprint.Font = PxFont("Consolas", 13f);
        _refuse.Font = PxFont("Segoe UI", 13f);
        _accept.Font = PxFont("Segoe UI Semibold", 13f);

        var full = ClientW - 2 * Pad;
        var y = 18;

        _title.SetBounds(P(Pad), P(y), P(full), P(26)); y += 34;
        _who.SetBounds(P(Pad), P(y), P(full), P(44)); y += 50;
        _fingerprint.SetBounds(P(Pad), P(y), P(full), P(22)); y += 30;
        _warn.SetBounds(P(Pad), P(y), P(full), P(56)); y += 64;

        _refuse.SetBounds(P(ClientW - Pad - 132), P(y), P(132), P(32));
        _accept.SetBounds(_refuse.Left - P(140), P(y), P(132), P(32));
        y += 32 + Pad;

        ClientSize = new Size(P(ClientW), P(y));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
