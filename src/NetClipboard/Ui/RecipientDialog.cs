using NetClipboard.Core;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Sceglie a chi mandare i file arrivati dal menu "Invia a" di Windows.
///
/// Qui compaiono ANCHE i propri dispositivi, al contrario del menu della tray.
/// Non è un'incoerenza: dal menu della tray si manda ciò che è già negli
/// appunti, e quindi i propri PC ce l'hanno già; qui invece i file arrivano da
/// Explorer senza passare dagli appunti, quindi mandarli al proprio portatile è
/// un'azione vera e non un doppione.
///
/// I due gruppi restano separati, perché la differenza continua a valere: i
/// propri dispositivi ricevono in silenzio, gli altri devono accettare.
/// </summary>
public sealed class RecipientDialog : ScaledForm
{
    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 420;
    private const int Pad = 18;
    private const int RowH = 30;

    private readonly Label _title = new();
    private readonly Label _subtitle = new() { ForeColor = Color.Gainsboro };
    private readonly ListBox _list = new()
    {
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(38, 38, 46),
        ForeColor = Color.White,
        IntegralHeight = false,
    };
    private readonly Button _send = new() { DialogResult = DialogResult.OK };
    private readonly Button _cancel = new() { DialogResult = DialogResult.Cancel };

    /// <summary>Destinatario scelto, valorizzato solo se la finestra esce con OK.</summary>
    public Peer? Chosen { get; private set; }

    public RecipientDialog(IReadOnlyList<Peer> peers, int fileCount, string sizeText)
    {
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        BackColor = Color.FromArgb(28, 28, 34);
        ForeColor = Color.White;

        Text = L.T("recipient.title");
        _title.Text = L.T("recipient.heading");
        _subtitle.Text = L.T("recipient.subtitle", fileCount, sizeText);
        _send.Text = L.T("recipient.send");
        _cancel.Text = L.T("common.cancel");

        foreach (var b in new[] { _send, _cancel })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(55, 55, 66);
        }
        _send.BackColor = Color.FromArgb(30, 120, 200);
        AcceptButton = _send;
        CancelButton = _cancel;

        // I propri prima: sono la destinazione più frequente e non chiedono nulla
        // a nessuno. Le voci non selezionabili fanno da intestazione di gruppo.
        var mine = peers.Where(p => p.Trusted).OrderBy(p => p.Label).ToList();
        var others = peers.Where(p => !p.Trusted).OrderBy(p => p.Label).ToList();

        if (mine.Count > 0)
        {
            _list.Items.Add(new Entry(null, L.T("recipient.groupMine")));
            foreach (var p in mine) _list.Items.Add(new Entry(p, "    " + p.Label));
        }
        if (others.Count > 0)
        {
            _list.Items.Add(new Entry(null, L.T("recipient.groupOthers")));
            foreach (var p in others) _list.Items.Add(new Entry(p, "    " + p.Label));
        }

        _list.SelectedIndexChanged += (_, _) =>
        {
            // Le intestazioni non sono destinazioni: si scivola alla voce dopo.
            if (_list.SelectedItem is Entry { Peer: null } && _list.SelectedIndex + 1 < _list.Items.Count)
                _list.SelectedIndex++;
            _send.Enabled = _list.SelectedItem is Entry { Peer: not null };
        };

        _list.DoubleClick += (_, _) => { if (Confirm()) { DialogResult = DialogResult.OK; Close(); } };
        _send.Click += (_, _) => { if (!Confirm()) DialogResult = DialogResult.None; };

        SelectFirstPeer();
        Controls.AddRange(new Control[] { _title, _subtitle, _list, _send, _cancel });
    }

    private void SelectFirstPeer()
    {
        for (var i = 0; i < _list.Items.Count; i++)
            if (_list.Items[i] is Entry { Peer: not null })
            {
                _list.SelectedIndex = i;
                return;
            }
        _send.Enabled = false;
    }

    private bool Confirm()
    {
        Chosen = (_list.SelectedItem as Entry)?.Peer;
        return Chosen != null;
    }

    protected override Font CreateBaseFont() => PxFont("Segoe UI", 12f);

    protected override void ApplyLayout()
    {
        _title.Font = PxFont("Segoe UI Semibold", 15f);
        _subtitle.Font = PxFont("Segoe UI", 11f);
        _list.ItemHeight = P(RowH - 8);

        var full = ClientW - 2 * Pad;
        var y = 16;

        _title.SetBounds(P(Pad), P(y), P(full), P(24)); y += 28;
        _subtitle.SetBounds(P(Pad), P(y), P(full), P(20)); y += 28;

        var rows = Math.Clamp(_list.Items.Count, 3, 8);
        _list.SetBounds(P(Pad), P(y), P(full), P(rows * (RowH - 8) + 8));
        y += rows * (RowH - 8) + 8 + 14;

        _cancel.SetBounds(P(ClientW - Pad - 104), P(y), P(104), P(32));
        _send.SetBounds(_cancel.Left - P(112), P(y), P(104), P(32));
        y += 32 + Pad;

        ClientSize = new Size(P(ClientW), P(y));
    }

    /// <summary>Voce dell'elenco: un destinatario, oppure un'intestazione se Peer è null.</summary>
    private sealed record Entry(Peer? Peer, string Text)
    {
        public override string ToString() => Text;
    }
}
