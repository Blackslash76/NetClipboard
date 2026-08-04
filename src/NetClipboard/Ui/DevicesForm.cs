using System.Net;
using NetClipboard.Core;
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
        UpdateList(_trusted, _trust.All
            .Select(d => (Key: d.DeviceId, C1: d.Name, C2: DeviceIdentity.ShortFingerprint(d.DeviceId), Tag: (object)d.DeviceId))
            .ToList());

        UpdateList(_discovered, _transport.Peers.Where(p => !p.Trusted)
            .Select(p => (Key: p.DeviceId, C1: p.Name, C2: p.Address.ToString(), Tag: (object)p))
            .ToList());
    }

    /// <summary>Aggiorna la ListView SENZA perdere la selezione: ricostruisce solo se il set cambia.</summary>
    private static void UpdateList(ListView lv, List<(string Key, string C1, string C2, object Tag)> items)
    {
        var existing = lv.Items.Cast<ListViewItem>().Select(KeyOf).ToList();
        var newKeys = items.Select(i => i.Key).ToList();

        if (existing.SequenceEqual(newKeys))
        {
            // stesso insieme: aggiorna solo i testi, la selezione resta intatta
            for (var k = 0; k < items.Count; k++)
            {
                lv.Items[k].Text = items[k].C1;
                lv.Items[k].SubItems[1].Text = items[k].C2;
                lv.Items[k].Tag = items[k].Tag;
            }
            return;
        }

        var selKey = lv.SelectedItems.Count > 0 ? KeyOf(lv.SelectedItems[0]) : null;
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var it in items)
        {
            var lvi = new ListViewItem(it.C1) { Tag = it.Tag };
            lvi.SubItems.Add(it.C2);
            lv.Items.Add(lvi);
        }
        lv.EndUpdate();

        if (selKey != null)
            foreach (ListViewItem lvi in lv.Items)
                if (KeyOf(lvi) == selKey) { lvi.Selected = true; lvi.Focused = true; break; }
    }

    private static string KeyOf(ListViewItem i) => i.Tag is Peer p ? p.DeviceId : (string)i.Tag!;

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
        if (_discovered.SelectedItems.Count == 0)
        {
            MessageBox.Show("Seleziona prima un dispositivo dall'elenco \"In rete\".",
                "Pairing", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
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
        if (_busy)
        {
            MessageBox.Show("Un pairing è già in corso.", "Pairing", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _busy = true;
        Log.Write($"[Pairing] avvio verso {name} @ {ip}:{port}");
        try
        {
            var (outcome, resolved) = await _transport.PairAsync(ip, port, name, CancellationToken.None);
            Log.Write($"[Pairing] esito verso {name}: {outcome}");
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
