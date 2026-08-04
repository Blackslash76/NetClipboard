using System.Net;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Gestione dispositivi: mostra l'identità di questo PC, i dispositivi fidati
/// (con revoca) e quelli in rete da accoppiare (con codice). Consente anche il
/// pairing per IP manuale. Interamente DPI-aware (vedi <see cref="ScaledForm"/>).
/// </summary>
public sealed class DevicesForm : ScaledForm
{
    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 560;
    private const int Pad = 16;
    private const int ListH = 152;
    private const int BtnH = 30;

    private readonly DeviceIdentity _identity;
    private readonly TrustStore _trust;
    private readonly ClipboardTransport _transport;

    private readonly Label _titleSelf = new() { Text = "Questo dispositivo" };
    private readonly Label _self;
    private readonly Label _titleTrusted = new() { Text = "Dispositivi fidati" };
    private readonly Label _titleDiscovered = new() { Text = "In rete — da accoppiare" };
    private readonly Label _lblManualIp = new() { Text = "Oppure per IP:" };

    private readonly ListView _trusted;
    private readonly ListView _discovered;
    private readonly TextBox _manualIp = new();
    private readonly Button _revoke = new() { Text = "Revoca selezionato" };
    private readonly Button _pair = new() { Text = "Accoppia selezionato" };
    private readonly Button _scan = new() { Text = "Cerca in rete" };
    private readonly Button _pairIp = new() { Text = "Accoppia per IP" };

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

        _self = new Label
        {
            Text = $"{Environment.MachineName}   ·   impronta {DeviceIdentity.ShortFingerprint(_identity.DeviceId)}",
            ForeColor = Color.DimGray,
        };
        _trusted = MakeList("Nome", "Impronta");
        _discovered = MakeList("Nome", "Indirizzo");

        _revoke.Click += (_, _) => RevokeSelected();
        _pair.Click += (_, _) => PairSelected();
        _scan.Click += (_, _) => _transport.ScanOnDemand();
        _pairIp.Click += (_, _) => PairManual();

        Controls.AddRange(new Control[]
        {
            _titleSelf, _self,
            _titleTrusted, _trusted, _revoke,
            _titleDiscovered, _discovered, _pair, _scan,
            _lblManualIp, _manualIp, _pairIp,
        });

        _refresh.Tick += (_, _) => RefreshLists();
        _refresh.Start();
        RefreshLists();
    }

    protected override void ApplyLayout()
    {
        var section = PxFont("Segoe UI Semibold", 13.5f);
        _titleSelf.Font = section;
        _titleTrusted.Font = section;
        _titleDiscovered.Font = section;

        var full = ClientW - 2 * Pad;
        var y = 14;

        _titleSelf.SetBounds(P(Pad), P(y), P(full), P(22)); y += 26;
        _self.SetBounds(P(Pad), P(y), P(full), P(20)); y += 30;

        _titleTrusted.SetBounds(P(Pad), P(y), P(300), P(22)); y += 26;
        _trusted.SetBounds(P(Pad), P(y), P(full), P(ListH)); y += ListH + 8;
        _revoke.SetBounds(P(Pad), P(y), P(170), P(BtnH)); y += BtnH + 14;

        _titleDiscovered.SetBounds(P(Pad), P(y), P(300), P(22)); y += 26;
        _discovered.SetBounds(P(Pad), P(y), P(full), P(ListH)); y += ListH + 8;
        _pair.SetBounds(P(Pad), P(y), P(180), P(BtnH));
        _scan.SetBounds(P(Pad + 190), P(y), P(130), P(BtnH)); y += BtnH + 16;

        _lblManualIp.SetBounds(P(Pad), P(y + 5), P(100), P(20));
        _manualIp.SetBounds(P(Pad + 104), P(y + 1), P(190), P(25));
        _pairIp.SetBounds(P(Pad + 304), P(y), P(150), P(28));
        y += 28 + Pad;

        ClientSize = new Size(P(ClientW), P(y));

        // le colonne non si scalano da sole
        foreach (var lv in new[] { _trusted, _discovered })
        {
            lv.Columns[0].Width = P(230);
            lv.Columns[1].Width = P(full - 230 - 24);
        }
    }

    private static ListView MakeList(string c1, string c2)
    {
        var lv = new ListView
        {
            View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        };
        lv.Columns.Add(c1);
        lv.Columns.Add(c2);
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

    // La X non chiude: nasconde nella tray (si esce solo con "Esci").
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (Visible) { RefreshLists(); _refresh.Start(); }
        else _refresh.Stop();
        base.OnVisibleChanged(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Dispose();
        base.OnFormClosed(e);
    }
}
