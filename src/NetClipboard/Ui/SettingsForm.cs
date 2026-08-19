using Microsoft.Win32;
using NetClipboard.Core;
using NetClipboard.Net;
using NetClipboard.Update;

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
    private const int ClientW = 600;
    private const int Pad = 20;
    private const int LabelW = 172;
    private const int CtlX = 196;

    /// <summary>Dove comincia la colonna di destra (le scelte a interruttore).</summary>
    private const int RightX = 300;
    private const int RowH = 36;
    private const int FieldH = 26;

    /// <summary>
    /// Larghezza unica dei campi numerici: la piu' stretta che regge il valore piu'
    /// lungo (la porta, 5 cifre). Larghezze diverse riga per riga facevano scaletta.
    /// </summary>
    private const int NumW = 64;

    /// <summary>Distanza fra le caselle di spunta in colonna.</summary>
    private const int CheckH = 30;

    private readonly AppConfig _config;

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
    private readonly CheckBox _sendToMenu = new() { AutoSize = true };
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

    private readonly Label _lblPort = new();
    private readonly Label _lblHistory = new();
    private readonly Label _lblVisibleRows = new();
    private readonly Label _lblAge = new();
    private readonly Label _lblSize = new();
    private readonly Label _lblShare = new();
    private readonly Label _lblUpdateUrl = new();
    private readonly Label _lblPeers = new();

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
        Theme.Attach(this, ApplyTheme);
    }

    /// <summary>Colori: base dal tema, poi le righe di spiegazione in tono minore.</summary>
    private void ApplyTheme()
    {
        Theme.ApplyToControls(this);
        _firewall.ForeColor = Theme.TextMuted;
    }

    /// <summary>Tutti i testi in un punto solo (catalogo <see cref="L"/>, mai literal inline).</summary>
    private void ApplyTexts()
    {
        // La versione la si cerca quando serve dirla a qualcuno: sta dove si guarda
        // per prima cosa, non in fondo a un menu.
        Text = L.T("settings.titleVersion", Updater.CurrentVersion.ToString(3));
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
        _sendToMenu.Text = L.T("settings.sendToMenu");
        _autoScan.Text = L.T("settings.autoScan");
        _autoUpdate.Text = L.T("settings.autoUpdate");
        _lblUpdateUrl.Text = L.T("settings.updateUrl");
        _updateUrl.PlaceholderText = L.T("settings.updateUrlPlaceholder");
        _lblPeers.Text = L.T("settings.manualPeers");
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
            _lblPort, _port,
            _lblHistory, _historySize,
            _lblVisibleRows, _visibleRows,
            _lblAge, _maxAgeDays,
            _lblSize, _maxMb,
            _lblShare, _shareText, _shareImages, _shareFiles,
            _autostart, _sendToMenu, _autoScan, _autoUpdate,
            _lblUpdateUrl, _updateUrl,
            _lblPeers, _manualPeers,
            _firewall, _firewallBtn,
            _lblMaintenance, _restartNetBtn, _logBtn, _updatesBtn,
            _ok, _cancel,
        });
    }

    protected override void ApplyLayout()
    {
        // Due colonne, non una.
        //
        // In colonna sola la finestra arrivava a 740 px logici: al 150% su uno
        // schermo 1080p non ci stava piu' e ScaledForm attivava lo scorrimento.
        // Una finestra di impostazioni che si scorre e' una finestra che nasconde
        // meta' di se stessa. Siccome la meta' destra era vuota, le scelte a
        // interruttore stanno li': la finestra diventa larga e bassa, e lo spazio
        // fra i controlli si puo' tenere.
        var y = Pad;

        // Etichetta a sinistra + controllo a destra, su una riga.
        void Row(Label label, Control ctl, int ctlW)
        {
            label.SetBounds(P(Pad), P(y + 5), P(LabelW), P(20));
            ctl.SetBounds(P(CtlX), P(y), P(ctlW), P(FieldH));
            y += RowH;
        }

        Row(_lblPort, _port, NumW);
        Row(_lblHistory, _historySize, NumW);
        Row(_lblVisibleRows, _visibleRows, NumW);
        Row(_lblAge, _maxAgeDays, NumW);
        Row(_lblSize, _maxMb, NumW);
        var leftBottom = y;

        // --- colonna di destra: che cosa condividere e che cosa fare da solo ---
        var ry = Pad;
        _lblShare.SetBounds(P(RightX), P(ry), P(ClientW - RightX - Pad), P(20));
        ry += 26;

        var x = P(RightX);
        foreach (var cb in new[] { _shareText, _shareImages, _shareFiles })
        {
            cb.Location = new Point(x, P(ry));
            x = cb.Right + P(14);
        }
        ry += 34;

        foreach (var cb in new[] { _autostart, _sendToMenu, _autoScan, _autoUpdate })
        {
            cb.Location = new Point(P(RightX), P(ry));
            ry += CheckH;
        }

        y = Math.Max(leftBottom, ry) + 14;

        // --- sotto, a tutta larghezza ---
        Row(_lblUpdateUrl, _updateUrl, ClientW - CtlX - Pad);

        _lblPeers.SetBounds(P(Pad), P(y + 5), P(LabelW), P(20));
        y += 28;
        _manualPeers.SetBounds(P(Pad), P(y), P(ClientW - 2 * Pad), P(70));
        y += 70 + 22;

        // Manutenzione: e' un blocco a se', e si stacca dal resto con il suo spazio
        // sopra e sotto invece che appoggiarsi alla riga precedente. I tre bottoni
        // partono subito dopo l'etichetta e non dalla colonna dei controlli: li'
        // "Aggiornamenti" non ci stava, e un pulsante col testo tagliato non dice
        // piu' che cosa fa.
        const int maintLabelW = 96;
        _lblMaintenance.SetBounds(P(Pad), P(y + 7), P(maintLabelW), P(20));
        var bx = Pad + maintLabelW + 12;
        var btnW = (ClientW - Pad - bx - 16) / 3;
        foreach (var b in new[] { _restartNetBtn, _logBtn, _updatesBtn })
        {
            b.SetBounds(P(bx), P(y), P(btnW), P(32));
            bx += btnW + 8;
        }
        y += 32 + 22;

        _firewall.MaximumSize = new Size(P(ClientW - 2 * Pad - 160), 0);
        _firewall.Location = new Point(P(Pad), P(y + 6));
        _firewallBtn.SetBounds(P(ClientW - Pad - 138), P(y), P(138), P(32));
        // il testo del firewall va a capo: l'altezza reale (pixel) torna in unità logiche
        y += Math.Max(48, (int)Math.Round(_firewall.Height / ScaleFactor) + 16);

        y += 10;
        _cancel.SetBounds(P(ClientW - Pad - 96), P(y), P(96), P(32));
        _ok.SetBounds(_cancel.Left - P(104), P(y), P(96), P(32));
        y += 32 + Pad;

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
        _sendToMenu.Checked = _config.SendToMenu;
        _autoScan.Checked = _config.AutoScanDiscovery;
        _autoUpdate.Checked = _config.AutoUpdateCheck;
        _updateUrl.Text = _config.UpdateManifestUrl;
        _manualPeers.Text = string.Join(Environment.NewLine, _config.ManualPeers);
    }

    private void SaveToConfig()
    {
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
        _config.SendToMenu = SendToShortcut.Apply(_sendToMenu.Checked);
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
