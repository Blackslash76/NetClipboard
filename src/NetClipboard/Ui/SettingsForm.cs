using Microsoft.Win32;
using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>Finestra impostazioni: passphrase, porta, cronologia, avvio automatico.</summary>
public sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NetClipboard";

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

    public SettingsForm(AppConfig config)
    {
        _config = config;

        Text = "NetClipboard · Impostazioni";
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 664);
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        LoadFromConfig();
    }

    private void BuildLayout()
    {
        var y = 16;
        int Row() { var v = y; y += 30; return v; }

        void AddLabel(string text, int rowY) =>
            Controls.Add(new Label { Text = text, Left = 16, Top = rowY + 3, Width = 150, AutoSize = false });

        AddLabel("Nome di questo PC", Row());
        _name.SetBounds(180, y - 30, 220, 24);
        Controls.Add(_name);

        AddLabel("Porta (TCP/UDP)", Row());
        _port.SetBounds(180, y - 30, 100, 24);
        Controls.Add(_port);

        AddLabel("Elementi in cronologia", Row());
        _historySize.SetBounds(180, y - 30, 100, 24);
        Controls.Add(_historySize);

        AddLabel("Conservazione (giorni, 0=∞)", Row());
        _maxAgeDays.SetBounds(180, y - 30, 100, 24);
        Controls.Add(_maxAgeDays);

        AddLabel("Dimensione max (MB)", Row());
        _maxMb.SetBounds(180, y - 30, 100, 24);
        Controls.Add(_maxMb);

        AddLabel("Condividi", Row());
        _shareText.Left = 180; _shareText.Top = y - 28;
        _shareImages.Left = 250; _shareImages.Top = y - 28;
        _shareFiles.Left = 340; _shareFiles.Top = y - 28;
        Controls.Add(_shareText);
        Controls.Add(_shareImages);
        Controls.Add(_shareFiles);

        y += 8;
        _autostart.Left = 180; _autostart.Top = y; y += 28;
        Controls.Add(_autostart);
        _autoScan.Left = 180; _autoScan.Top = y; y += 28;
        Controls.Add(_autoScan);
        _autoUpdate.Left = 180; _autoUpdate.Top = y; y += 30;
        Controls.Add(_autoUpdate);

        AddLabel("URL update (opz.)", Row());
        _updateUrl.SetBounds(180, y - 30, 224, 24);
        Controls.Add(_updateUrl);

        // IP peer manuali (quando il broadcast è bloccato)
        Controls.Add(new Label { Text = "IP peer manuali", Left = 16, Top = y + 3, Width = 150 });
        Controls.Add(new Label
        {
            Text = "(uno per riga; basta che un lato inserisca l'IP dell'altro)",
            Left = 180, Top = y + 3, Width = 224, Height = 30,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 7.5f),
        });
        y += 34;
        _manualPeers.SetBounds(16, y, 388, 66);
        Controls.Add(_manualPeers);
        y += 76;

        // Stato firewall + pulsante
        _firewall.Left = 16; _firewall.Top = y + 4; _firewall.MaximumSize = new Size(280, 0);
        Controls.Add(_firewall);
        var fwBtn = new Button { Text = "Configura firewall", Left = 300, Top = y, Width = 104, Height = 26 };
        fwBtn.Click += (_, _) =>
        {
            if (FirewallHelper.IsElevated())
                FirewallHelper.InstallRulesNow();
            else
                FirewallHelper.RequestInstallElevated();
            UpdateFirewallLabel();
        };
        Controls.Add(fwBtn);
        y += 44;

        var ok = new Button { Text = "Salva", Left = 234, Top = y, Width = 80 };
        var cancel = new Button { Text = "Annulla", Left = 322, Top = y, Width = 80 };
        ok.Click += (_, _) => { SaveToConfig(); Saved?.Invoke(); Hide(); };
        cancel.Click += (_, _) => { LoadFromConfig(); Hide(); };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        UpdateFirewallLabel();
    }

    /// <summary>Scatta quando l'utente salva le impostazioni.</summary>
    public event Action? Saved;

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
