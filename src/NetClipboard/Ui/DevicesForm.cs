using System.Net;
using NetClipboard.Core.Security;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Gestione dispositivi: mostra l'identità di questo PC, i dispositivi fidati
/// (con revoca) e quelli in rete da accoppiare (con codice). Consente anche il
/// pairing per IP manuale.
/// </summary>
public sealed class DevicesForm : Form
{
    private readonly DeviceIdentity _identity;
    private readonly TrustStore _trust;
    private readonly ClipboardTransport _transport;

    private readonly ListView _trusted;
    private readonly ListView _discovered;
    private readonly TextBox _manualIp;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    private bool _busy;

    public DevicesForm(DeviceIdentity identity, TrustStore trust, ClipboardTransport transport)
    {
        _identity = identity;
        _trust = trust;
        _transport = transport;

        Text = "NetClipboard · Dispositivi";
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(540, 560);
        Font = new Font("Segoe UI", 9f);

        Controls.Add(new Label
        {
            Text = "Questo dispositivo",
            Left = 16, Top = 14, Width = 500, Font = new Font("Segoe UI Semibold", 10f),
        });
        Controls.Add(new Label
        {
            Text = $"{Environment.MachineName}   ·   impronta {DeviceIdentity.ShortFingerprint(_identity.DeviceId)}",
            Left = 16, Top = 38, Width = 500, ForeColor = Color.DimGray,
        });

        Controls.Add(new Label { Text = "Dispositivi fidati", Left = 16, Top = 70, Width = 300, Font = new Font("Segoe UI Semibold", 10f) });
        _trusted = MakeList(16, 94, 508, 150, "Nome", "Impronta");
        Controls.Add(_trusted);
        var revoke = new Button { Text = "Revoca selezionato", Left = 16, Top = 248, Width = 160, Height = 28 };
        revoke.Click += (_, _) => RevokeSelected();
        Controls.Add(revoke);

        Controls.Add(new Label { Text = "In rete — da accoppiare", Left = 16, Top = 288, Width = 300, Font = new Font("Segoe UI Semibold", 10f) });
        _discovered = MakeList(16, 312, 508, 150, "Nome", "Indirizzo");
        Controls.Add(_discovered);
        var pair = new Button { Text = "Accoppia selezionato", Left = 16, Top = 466, Width = 170, Height = 28 };
        pair.Click += (_, _) => PairSelected();
        Controls.Add(pair);

        var scan = new Button { Text = "Cerca in rete", Left = 196, Top = 466, Width = 120, Height = 28 };
        scan.Click += (_, _) => _transport.ScanOnDemand();
        Controls.Add(scan);

        Controls.Add(new Label { Text = "Oppure per IP:", Left = 16, Top = 514, Width = 90 });
        _manualIp = new TextBox { Left = 110, Top = 510, Width = 200 };
        Controls.Add(_manualIp);
        var pairIp = new Button { Text = "Accoppia per IP", Left = 320, Top = 508, Width = 140, Height = 26 };
        pairIp.Click += (_, _) => PairManual();
        Controls.Add(pairIp);

        _refresh.Tick += (_, _) => RefreshLists();
        _refresh.Start();
        RefreshLists();
    }

    private static ListView MakeList(int x, int y, int w, int h, string c1, string c2)
    {
        var lv = new ListView
        {
            Left = x, Top = y, Width = w, Height = h,
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        };
        lv.Columns.Add(c1, 220);
        lv.Columns.Add(c2, 270);
        return lv;
    }

    private void RefreshLists()
    {
        // Fidati
        _trusted.BeginUpdate();
        _trusted.Items.Clear();
        foreach (var d in _trust.All)
        {
            var it = new ListViewItem(d.Name) { Tag = d.DeviceId };
            it.SubItems.Add(DeviceIdentity.ShortFingerprint(d.DeviceId));
            _trusted.Items.Add(it);
        }
        _trusted.EndUpdate();

        // Scoperti non fidati
        _discovered.BeginUpdate();
        _discovered.Items.Clear();
        foreach (var p in _transport.Peers.Where(p => !p.Trusted))
        {
            var it = new ListViewItem(p.Name) { Tag = p };
            it.SubItems.Add(p.Address.ToString());
            _discovered.Items.Add(it);
        }
        _discovered.EndUpdate();
    }

    private void RevokeSelected()
    {
        if (_trusted.SelectedItems.Count == 0) return;
        var deviceId = (string)_trusted.SelectedItems[0].Tag!;
        var name = _trusted.SelectedItems[0].Text;
        if (MessageBox.Show($"Revocare la fiducia a \"{name}\"?", "Revoca",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _trust.Revoke(deviceId);
            RefreshLists();
        }
    }

    private void PairSelected()
    {
        if (_discovered.SelectedItems.Count == 0) return;
        var peer = (Peer)_discovered.SelectedItems[0].Tag!;
        StartPairing(peer.Address, peer.Port, peer.Name);
    }

    private void PairManual()
    {
        if (!IPAddress.TryParse(_manualIp.Text.Trim(), out var ip))
        {
            MessageBox.Show("Indirizzo IP non valido.", "Pairing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        StartPairing(ip, _transport.Port, ip.ToString());
    }

    private async void StartPairing(IPAddress ip, int port, string name)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (outcome, resolved) = await _transport.PairAsync(ip, port, name, CancellationToken.None);
            RefreshLists();
            var msg = outcome switch
            {
                PairOutcome.Paired => $"Accoppiato con \"{resolved}\".",
                PairOutcome.Rejected => "Pairing rifiutato (codice non confermato su un lato).",
                _ => "Pairing non riuscito (dispositivo non raggiungibile?).",
            };
            MessageBox.Show(msg, "Pairing", MessageBoxButtons.OK,
                outcome == PairOutcome.Paired ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally { _busy = false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        base.OnFormClosed(e);
    }
}
