using Microsoft.Win32;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Finestra impostazioni: nome, porta, cronologia, condivisione, avvio automatico.
/// Interamente DPI-aware (vedi <see cref="ScaledForm"/>).
/// </summary>
public sealed class SettingsForm : ScaledForm
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetClipboard";

    // Misure logiche (a 96 DPI), scalate da P().
    private const int ClientW = 470;
    private const int Pad = 16;
    private const int LabelW = 178;
    private const int CtlX = 202;
    private const int RowH = 32;
    private const int FieldH = 25;

    private readonly AppConfig _config;

    private readonly TextBox _name = new();
    private readonly NumericUpDown _port = new() { Minimum = 1024, Maximum = 65535 };
    private readonly NumericUpDown _historySize = new() { Minimum = 5, Maximum = 200 };
    private readonly NumericUpDown _maxAgeDays = new() { Minimum = 0, Maximum = 3650 };
    private readonly NumericUpDown _maxMb = new() { Minimum = 1, Maximum = 2048 };
    private readonly CheckBox _shareText = new() { Text = "Testo", AutoSize = true };
    private readonly CheckBox _shareImages = new() { Text = "Immagini", AutoSize = true };
    private readonly CheckBox _shareFiles = new() { Text = "File", AutoSize = true };
    private readonly CheckBox _autostart = new() { Text = "Avvia con Windows", AutoSize = true };
    private readonly CheckBox _autoScan = new() { Text = "Scoperta automatica (scansione rete)", AutoSize = true };
    private readonly CheckBox _autoUpdate = new() { Text = "Controlla aggiornamenti", AutoSize = true };
    private readonly TextBox _updateUrl = new() { PlaceholderText = "predefinito (già incluso) · lascia vuoto" };
    private readonly TextBox _manualPeers = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _firewall = new() { AutoSize = true };
    private readonly Button _firewallBtn = new() { Text = "Configura firewall" };
    private readonly Button _ok = new() { Text = "Salva" };
    private readonly Button _cancel = new() { Text = "Annulla" };

    private readonly Label _lblName = new() { Text = "Nome di questo PC" };
    private readonly Label _lblPort = new() { Text = "Porta (TCP/UDP)" };
    private readonly Label _lblHistory = new() { Text = "Elementi in cronologia" };
    private readonly Label _lblAge = new() { Text = "Conservazione (giorni, 0=∞)" };
    private readonly Label _lblSize = new() { Text = "Dimensione max (MB)" };
    private readonly Label _lblShare = new() { Text = "Condividi" };
    private readonly Label _lblUpdateUrl = new() { Text = "URL update (opz.)" };
    private readonly Label _lblPeers = new() { Text = "IP peer manuali" };
    private readonly Label _lblPeersHint = new()
    {
        Text = "(uno per riga; basta che un lato inserisca l'IP dell'altro)",
        ForeColor = Color.Gray,
    };

    public SettingsForm(AppConfig config)
    {
        _config = config;

        Text = "NetClipboard · Impostazioni";
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildControls();
        LoadFromConfig();
    }

    /// <summary>Scatta quando l'utente salva le impostazioni.</summary>
    public event Action? Saved;

    private void BuildControls()
    {
        _firewallBtn.Click += (_, _) =>
        {
            if (FirewallHelper.IsElevated())
                FirewallHelper.InstallRulesNow();
            else
                FirewallHelper.RequestInstallElevated();
            UpdateFirewallLabel();
        };
        _ok.Click += (_, _) => { SaveToConfig(); Saved?.Invoke(); Hide(); };
        _cancel.Click += (_, _) => { LoadFromConfig(); Hide(); };
        AcceptButton = _ok;
        CancelButton = _cancel;

        Controls.AddRange(new Control[]
        {
            _lblName, _name,
            _lblPort, _port,
            _lblHistory, _historySize,
            _lblAge, _maxAgeDays,
            _lblSize, _maxMb,
            _lblShare, _shareText, _shareImages, _shareFiles,
            _autostart, _autoScan, _autoUpdate,
            _lblUpdateUrl, _updateUrl,
            _lblPeers, _lblPeersHint, _manualPeers,
            _firewall, _firewallBtn,
            _ok, _cancel,
        });

        UpdateFirewallLabel();
    }

    protected override void ApplyLayout()
    {
        var hint = PxFont("Segoe UI", 10f);
        _lblPeersHint.Font = hint;

        var y = Pad;
        var fieldW = ClientW - CtlX - Pad;

        // Etichetta a sinistra + controllo a destra, su una riga.
        void Row(Label label, Control ctl, int ctlW)
        {
            label.SetBounds(P(Pad), P(y + 4), P(LabelW), P(20));
            ctl.SetBounds(P(CtlX), P(y), P(ctlW), P(FieldH));
            y += RowH;
        }

        Row(_lblName, _name, fieldW);
        Row(_lblPort, _port, 110);
        Row(_lblHistory, _historySize, 110);
        Row(_lblAge, _maxAgeDays, 110);
        Row(_lblSize, _maxMb, 110);

        // Riga "Condividi": tre checkbox in fila, larghezza dettata dal testo.
        _lblShare.SetBounds(P(Pad), P(y + 4), P(LabelW), P(20));
        var x = P(CtlX);
        foreach (var cb in new[] { _shareText, _shareImages, _shareFiles })
        {
            cb.Location = new Point(x, P(y + 3));
            x = cb.Right + P(12);
        }
        y += RowH;

        y += 6;
        foreach (var cb in new[] { _autostart, _autoScan, _autoUpdate })
        {
            cb.Location = new Point(P(CtlX), P(y));
            y += 28;
        }
        y += 4;

        Row(_lblUpdateUrl, _updateUrl, fieldW);

        _lblPeers.SetBounds(P(Pad), P(y + 4), P(LabelW), P(20));
        _lblPeersHint.SetBounds(P(CtlX), P(y), P(fieldW), P(32));
        y += 36;
        _manualPeers.SetBounds(P(Pad), P(y), P(ClientW - 2 * Pad), P(70));
        y += 80;

        _firewall.MaximumSize = new Size(P(ClientW - 2 * Pad - 150), 0);
        _firewall.Location = new Point(P(Pad), P(y + 4));
        _firewallBtn.SetBounds(P(ClientW - Pad - 138), P(y), P(138), P(28));
        // il testo del firewall va a capo: l'altezza reale (pixel) torna in unità logiche
        y += Math.Max(44, (int)Math.Round(_firewall.Height / ScaleFactor) + 14);

        _cancel.SetBounds(P(ClientW - Pad - 92), P(y), P(92), P(28));
        _ok.SetBounds(_cancel.Left - P(100), P(y), P(92), P(28));
        y += 28 + Pad;

        ClientSize = new Size(P(ClientW), Math.Max(P(y), _ok.Bottom + P(Pad)));
    }

    // La X non chiude: nasconde nella tray (si esce solo con "Esci").
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            LoadFromConfig(); // scarta modifiche non salvate
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (Visible)
        {
            LoadFromConfig();
            UpdateFirewallLabel();
        }
        base.OnVisibleChanged(e);
    }

    private void UpdateFirewallLabel()
    {
        _firewall.Text = FirewallHelper.IsElevated()
            ? "Regola firewall: puoi configurarla ora (sei admin)."
            : "Se i PC non si vedono, configura la regola firewall (richiede admin).";
    }

    private void LoadFromConfig()
    {
        _name.Text = _config.DisplayName;
        _port.Value = Math.Clamp(_config.Port, 1024, 65535);
        _historySize.Value = Math.Clamp(_config.HistorySize, 5, 200);
        _maxAgeDays.Value = Math.Clamp(_config.HistoryMaxAgeDays, 0, 3650);
        _maxMb.Value = Math.Clamp(_config.MaxTransferMb, 1, 2048);
        _shareText.Checked = _config.ShareText;
        _shareImages.Checked = _config.ShareImages;
        _shareFiles.Checked = _config.ShareFiles;
        _autostart.Checked = _config.StartWithWindows;
        _autoScan.Checked = _config.AutoScanDiscovery;
        _autoUpdate.Checked = _config.AutoUpdateCheck;
        _updateUrl.Text = _config.UpdateManifestUrl;
        _manualPeers.Text = string.Join(Environment.NewLine, _config.ManualPeers);
    }

    private void SaveToConfig()
    {
        _config.DisplayName = string.IsNullOrWhiteSpace(_name.Text) ? Environment.MachineName : _name.Text.Trim();
        _config.Port = (int)_port.Value;
        _config.HistorySize = (int)_historySize.Value;
        _config.HistoryMaxAgeDays = (int)_maxAgeDays.Value;
        _config.MaxTransferMb = (int)_maxMb.Value;
        _config.ShareText = _shareText.Checked;
        _config.ShareImages = _shareImages.Checked;
        _config.ShareFiles = _shareFiles.Checked;
        _config.StartWithWindows = _autostart.Checked;
        _config.AutoScanDiscovery = _autoScan.Checked;
        _config.AutoUpdateCheck = _autoUpdate.Checked;
        _config.UpdateManifestUrl = _updateUrl.Text.Trim();
        _config.ManualPeers = _manualPeers.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
        _config.Save();

        ApplyAutoStart(_config.StartWithWindows);
    }

    public static void ApplyAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
                return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe != null)
                    key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch { }
    }
}
