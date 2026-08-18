using Microsoft.Win32;
using NetClipboard.Core;
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
    private readonly NumericUpDown _visibleRows = new()
    {
        Minimum = AppConfig.MinVisibleRows,
        Maximum = AppConfig.MaxVisibleRows,
    };
    private readonly NumericUpDown _maxAgeDays = new() { Minimum = 0, Maximum = 3650 };
    private readonly NumericUpDown _maxMb = new() { Minimum = 1, Maximum = 2048 };
    private readonly CheckBox _shareText = new() { AutoSize = true };
    private readonly CheckBox _shareImages = new() { AutoSize = true };
    private readonly CheckBox _shareFiles = new() { AutoSize = true };
    private readonly CheckBox _autostart = new() { AutoSize = true };
    private readonly CheckBox _autoScan = new() { AutoSize = true };
    private readonly CheckBox _autoUpdate = new() { AutoSize = true };
    private readonly TextBox _updateUrl = new();
    private readonly TextBox _manualPeers = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _firewall = new() { AutoSize = true };
    private readonly Button _firewallBtn = new();

    // Manutenzione: voci tolte dal menu della tray, che deve restare corto.
    private readonly Label _lblMaintenance = new();
    private readonly Button _restartNetBtn = new();
    private readonly Button _logBtn = new();
    private readonly Button _updatesBtn = new();
    private readonly Button _ok = new();
    private readonly Button _cancel = new();

    private readonly Label _lblName = new();
    private readonly Label _lblPort = new();
    private readonly Label _lblHistory = new();
    private readonly Label _lblVisibleRows = new();
    private readonly Label _lblAge = new();
    private readonly Label _lblSize = new();
    private readonly Label _lblShare = new();
    private readonly Label _lblUpdateUrl = new();
    private readonly Label _lblPeers = new();
    private readonly Label _lblPeersHint = new() { ForeColor = Color.Gray };

    public SettingsForm(AppConfig config)
    {
        _config = config;

        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildControls();
        ApplyTexts();
        LoadFromConfig();
    }

    /// <summary>Tutti i testi in un punto solo (catalogo <see cref="L"/>, mai literal inline).</summary>
    private void ApplyTexts()
    {
        Text = L.T("settings.title");
        _lblName.Text = L.T("settings.name");
        _lblPort.Text = L.T("settings.port");
        _lblHistory.Text = L.T("settings.historySize");
        _lblVisibleRows.Text = L.T("settings.visibleRows");
        _lblAge.Text = L.T("settings.maxAge");
        _lblSize.Text = L.T("settings.maxSize");
        _lblShare.Text = L.T("settings.share");
        _shareText.Text = L.T("settings.shareText");
        _shareImages.Text = L.T("settings.shareImages");
        _shareFiles.Text = L.T("settings.shareFiles");
        _autostart.Text = L.T("settings.autostart");
        _autoScan.Text = L.T("settings.autoScan");
        _autoUpdate.Text = L.T("settings.autoUpdate");
        _lblUpdateUrl.Text = L.T("settings.updateUrl");
        _updateUrl.PlaceholderText = L.T("settings.updateUrlPlaceholder");
        _lblPeers.Text = L.T("settings.manualPeers");
        _lblPeersHint.Text = L.T("settings.manualPeersHint");
        _firewallBtn.Text = L.T("settings.firewallButton");
        _lblMaintenance.Text = L.T("settings.maintenance");
        _restartNetBtn.Text = L.T("settings.restartNetwork");
        _logBtn.Text = L.T("settings.openLog");
        _updatesBtn.Text = L.T("settings.checkUpdates");
        _ok.Text = L.T("common.save");
        _cancel.Text = L.T("common.cancel");
        UpdateFirewallLabel();
    }

    /// <summary>Scatta quando l'utente salva le impostazioni.</summary>
    public event Action? Saved;

    // Le azioni vivono nella tray (che possiede rete, log e updater): qui ci sono
    // solo i bottoni che le richiamano. Campi e non proprieta': su un Form una
    // proprieta' pubblica non serializzabile fa scattare l'analizzatore WinForms.
    public Action? RestartNetworkRequested;
    public Action? OpenLogRequested;
    /// <summary>Attendibile: il download puo' durare, e il pulsante lo deve far vedere.</summary>
    public Func<Task>? CheckUpdatesRequested;

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
        _restartNetBtn.Click += (_, _) => RestartNetworkRequested?.Invoke();
        _logBtn.Click += (_, _) => OpenLogRequested?.Invoke();
        _updatesBtn.Click += async (_, _) =>
        {
            if (CheckUpdatesRequested == null) return;

            // Scaricare l'aggiornamento richiede tempo: senza un segno, il
            // pulsante sembrerebbe non aver fatto nulla.
            var normal = _updatesBtn.Text;
            _updatesBtn.Enabled = false;
            _updatesBtn.Text = L.T("settings.checkingUpdates");
            try { await CheckUpdatesRequested(); }
            finally
            {
                if (!IsDisposed) { _updatesBtn.Text = normal; _updatesBtn.Enabled = true; }
            }
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
            _lblVisibleRows, _visibleRows,
            _lblAge, _maxAgeDays,
            _lblSize, _maxMb,
            _lblShare, _shareText, _shareImages, _shareFiles,
            _autostart, _autoScan, _autoUpdate,
            _lblUpdateUrl, _updateUrl,
            _lblPeers, _lblPeersHint, _manualPeers,
            _firewall, _firewallBtn,
            _lblMaintenance, _restartNetBtn, _logBtn, _updatesBtn,
            _ok, _cancel,
        });
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
        Row(_lblVisibleRows, _visibleRows, 110);
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

        // Tre bottoni in fila, stessa larghezza, sull'intera riga utile.
        _lblMaintenance.SetBounds(P(Pad), P(y + 6), P(LabelW), P(20));
        var btnW = (ClientW - CtlX - Pad - 16) / 3;
        var bx = CtlX;
        foreach (var b in new[] { _restartNetBtn, _logBtn, _updatesBtn })
        {
            b.SetBounds(P(bx), P(y), P(btnW), P(28));
            bx += btnW + 8;
        }
        y += 40;

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
        _firewall.Text = L.T(FirewallHelper.IsElevated() ? "settings.firewallAdmin" : "settings.firewallHint");
    }

    private void LoadFromConfig()
    {
        _name.Text = _config.DisplayName;
        _port.Value = Math.Clamp(_config.Port, 1024, 65535);
        _historySize.Value = Math.Clamp(_config.HistorySize, 5, 200);
        _visibleRows.Value = Math.Clamp(_config.HistoryVisibleRows,
                                        AppConfig.MinVisibleRows, AppConfig.MaxVisibleRows);
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
        _config.HistoryVisibleRows = (int)_visibleRows.Value;
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
