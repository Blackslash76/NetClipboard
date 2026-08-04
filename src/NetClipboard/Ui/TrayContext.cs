using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using NetClipboard.Core;
using NetClipboard.Net;
using NetClipboard.Update;

namespace NetClipboard.Ui;

/// <summary>
/// Cuore dell'app: vive nel system tray e collega clipboard, cronologia, scoperta
/// dei peer e trasporto. Nessuna finestra principale.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly SecureChannel _channel;
    private readonly OfferStore _offerStore;
    private readonly ClipboardHistory _history;
    private readonly ClipboardMonitor _monitor;
    private readonly PeerDiscovery _discovery;
    private readonly ClipboardTransport _transport;
    private readonly HistoryForm _historyForm;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _sharingItem;
    private readonly ToolStripMenuItem _devicesItem;
    private readonly ToolStripMenuItem _updateItem;

    private System.Threading.Timer? _updateTimer;
    private string? _pendingUpdatePath;
    private Version? _pendingUpdateVersion;

    private bool _sharingEnabled;
    private bool _warnedSize;

    public TrayContext()
    {
        _config = AppConfig.Load();
        _config.Save();

        Log.Start($"NetClipboard avviato · {_config.DisplayName} · v{Updater.CurrentVersion} · porta {_config.Port} · " +
                  $"password={(_config.HasPassword ? "impostata" : "MANCANTE")}");
        Updater.CleanupOld(); // rimuove l'eseguibile .old di un update precedente

        _channel = new SecureChannel();
        _channel.SetPassword(_config.Password);

        _offerStore = new OfferStore();
        _history = new ClipboardHistory(_config);
        ClipboardHistory.CleanupReceived(_config.HistoryMaxAgeDays);

        _monitor = new ClipboardMonitor(_config);
        _ = _monitor.Handle; // forza la creazione dell'handle (senza mostrare)

        _discovery = new PeerDiscovery(_config, _channel);
        _transport = new ClipboardTransport(_config, _channel, _offerStore);
        _historyForm = new HistoryForm(_history);

        _sharingEnabled = _config.StartSharingEnabled;

        var menu = new ContextMenuStrip();
        _sharingItem = new ToolStripMenuItem("Condivisione attiva", null, (_, _) => ToggleSharing())
        {
            Checked = _sharingEnabled,
        };
        menu.Items.Add(_sharingItem);
        menu.Items.Add(new ToolStripMenuItem("Apri cronologia  (Win+Alt+V)", null, (_, _) => ShowHistory()));
        menu.Items.Add(new ToolStripMenuItem("Invia clipboard ora", null, (_, _) => SendCurrentClipboard()));
        menu.Items.Add(new ToolStripSeparator());
        _devicesItem = new ToolStripMenuItem("Dispositivi") { Enabled = false };
        menu.Items.Add(_devicesItem);
        menu.Items.Add(new ToolStripMenuItem("Cerca dispositivi in rete", null, (_, _) =>
        {
            _transport.ScanOnDemand();
            _tray!.ShowBalloonTip(1500, "NetClipboard", "Scansione della rete avviata…", ToolTipIcon.Info);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Impostazioni…", null, (_, _) => OpenSettings(false)));
        menu.Items.Add(new ToolStripMenuItem("Configura firewall (admin)", null, (_, _) => ConfigureFirewall()));
        menu.Items.Add(new ToolStripMenuItem("Riavvia rete", null, (_, _) =>
        {
            RestartNetwork();
            _tray!.ShowBalloonTip(1500, "NetClipboard", "Rete riavviata.", ToolTipIcon.Info);
        }));
        menu.Items.Add(new ToolStripMenuItem("Apri log diagnostico", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("Controlla aggiornamenti", null, (_, _) => _ = CheckForUpdateAsync(true)));
        _updateItem = new ToolStripMenuItem("Installa aggiornamento e riavvia", null, (_, _) => InstallPendingUpdate())
        {
            Visible = false,
        };
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Esci", null, (_, _) => ExitApp()));
        menu.Opening += (_, _) => RefreshDevicesMenu();

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create(_sharingEnabled),
            Text = "NetClipboard",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowHistory();

        _monitor.ClipboardChanged += OnLocalClipboard;
        _monitor.HistoryHotkeyPressed += ShowHistory;
        _transport.Received += OnRemoteReceived;
        _transport.PeerSeen += info => _discovery.ReportPeer(info.Id, info.Name, info.Address, info.Port);
        _transport.KnownPeersProvider = () =>
            _discovery.Peers.Select(p => new PeerInfo(p.Id, p.Name, p.Address, p.TcpPort));
        _discovery.PeersChanged += OnPeersChanged;
        _historyForm.ItemChosen += OnHistoryItemChosen;

        UpdateTrayText();

        if (_channel.HasKey)
            StartNetwork();
        else
            _monitor.BeginInvoke(() => OpenSettings(firstRun: true));

        StartUpdateChecks();
    }

    // ----- Auto-update -----

    private void StartUpdateChecks()
    {
        if (!_config.AutoUpdateCheck || !Updater.IsConfigured(_config.UpdateManifestUrl))
            return;
        _updateTimer = new System.Threading.Timer(
            _ => _ = CheckForUpdateAsync(false), null,
            TimeSpan.FromSeconds(8), TimeSpan.FromHours(6));
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        var url = _config.UpdateManifestUrl;
        if (!Updater.IsConfigured(url))
        {
            if (manual)
                Balloon("Aggiornamenti", "Non configurati: imposta l'URL in Impostazioni.", ToolTipIcon.Info);
            return;
        }
        if (manual)
            Balloon("Aggiornamenti", "Controllo in corso…");

        var info = await Updater.CheckAsync(url, CancellationToken.None);
        if (info == null)
        {
            if (manual)
                Balloon("Aggiornamenti", "Nessun aggiornamento disponibile.");
            return;
        }

        var path = await Updater.DownloadAsync(info, CancellationToken.None);
        if (path == null)
        {
            Balloon("Aggiornamenti", "Download dell'aggiornamento fallito (vedi log).", ToolTipIcon.Warning);
            return;
        }

        _pendingUpdatePath = path;
        _pendingUpdateVersion = info.Version;
        if (_monitor.IsHandleCreated)
            _monitor.BeginInvoke(() =>
            {
                _updateItem.Text = $"Installa aggiornamento v{info.Version} e riavvia";
                _updateItem.Visible = true;
                _tray.ShowBalloonTip(4000, "Aggiornamento disponibile",
                    $"v{info.Version} pronto. Installa dal menu del tray.", ToolTipIcon.Info);
            });
    }

    private void InstallPendingUpdate()
    {
        if (_pendingUpdatePath == null || !File.Exists(_pendingUpdatePath))
        {
            _tray.ShowBalloonTip(2000, "Aggiornamenti", "Nessun aggiornamento pronto.", ToolTipIcon.Warning);
            return;
        }
        if (Updater.ApplyAndRestart(_pendingUpdatePath))
        {
            _tray.Visible = false;
            _updateTimer?.Dispose();
            _discovery.Dispose();
            _transport.Dispose();
            _tray.Dispose();
            ExitThread();
        }
        else
        {
            _tray.ShowBalloonTip(3000, "Aggiornamenti", "Installazione fallita (vedi log).", ToolTipIcon.Warning);
        }
    }

    private void StartNetwork()
    {
        Log.Write("[App] avvio rete (discovery + transport)");
        _discovery.Start();
        _transport.Start();
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath))
                Log.Write("(log)");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(2000, "NetClipboard", $"Impossibile aprire il log: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void RestartNetwork()
    {
        _discovery.Stop();
        _transport.Stop();
        if (_channel.HasKey)
            StartNetwork();
    }

    // ----- Clipboard locale -> cronologia + (eventuale) push -----

    private void OnLocalClipboard(ClipboardPayload payload)
    {
        // I file diventano un'offerta servibile su richiesta.
        if (payload.Kind == PayloadKind.Files && payload.Offer != null)
            _offerStore.Register(payload.Offer);

        _history.Add(payload, _config.DisplayName, isLocal: true);

        if (!_sharingEnabled || !_channel.HasKey)
            return;
        if (!IsShareable(payload))
            return;
        if (payload.Kind != PayloadKind.Files && ExceedsSize(payload))
        {
            WarnSizeOnce();
            return;
        }

        var peers = _discovery.Peers;
        if (peers.Count > 0)
            _ = _transport.SendAsync(payload, peers);
    }

    // ----- Ricezione push da un peer -----

    private void OnRemoteReceived(ReceivedClip clip)
    {
        var name = ResolvePeerName(clip.FromId, clip.FromName);
        _monitor.BeginInvoke(() =>
        {
            _history.Add(clip.Payload, name, isLocal: false);

            // Mirror automatico solo per testo/immagine; i file restano "segnaposto".
            if (clip.Payload.Kind != PayloadKind.Files && _sharingEnabled)
                _monitor.ApplyToClipboard(clip.Payload);

            var hint = clip.Payload.Kind == PayloadKind.Files ? "  ·  Ctrl+Alt+V per incollare" : "";
            _tray.ShowBalloonTip(2500, $"Da {name}", clip.Payload.ShortPreview() + hint, ToolTipIcon.Info);
        });
    }

    private string ResolvePeerName(Guid id, string fallback) =>
        _discovery.Peers.FirstOrDefault(p => p.Id == id)?.Name ?? fallback;

    // ----- Scelta dalla cronologia -----

    private void OnHistoryItemChosen(HistoryItem item)
    {
        if (item.Kind != PayloadKind.Files)
        {
            var payload = _history.ToPayload(item);
            if (payload != null)
                _monitor.BeginInvoke(() => _monitor.ApplyToClipboard(payload));
            else
                Balloon("NetClipboard", "Contenuto non piu' disponibile.", ToolTipIcon.Warning);
            return;
        }

        // File/cartelle: materializza (riusa il locale o scarica dall'host).
        _ = Task.Run(() => MaterializeAsync(item));
    }

    private async Task MaterializeAsync(HistoryItem item)
    {
        try
        {
            if (item.LocalRootPaths is { Count: > 0 } && item.LocalRootPaths.All(Exists))
            {
                ApplyFiles(item.LocalRootPaths);
                Balloon("NetClipboard", "Pronto da incollare.");
                return;
            }
            if (item.IsLocalOffer)
            {
                Balloon("NetClipboard", "I file originali non sono piu' disponibili.", ToolTipIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(item.OwnerId) || string.IsNullOrEmpty(item.OfferId))
                return;

            var ownerId = Guid.Parse(item.OwnerId);
            var offerId = Guid.Parse(item.OfferId);
            var owner = _discovery.Peers.FirstOrDefault(p => p.Id == ownerId);
            if (owner == null)
            {
                Balloon("NetClipboard", $"{item.OwnerName} non e' in linea: impossibile scaricare.", ToolTipIcon.Warning);
                return;
            }

            Balloon("NetClipboard", $"Scarico da {item.OwnerName} · {ClipboardPayload.HumanSize(item.TotalSize)}…");
            var destDir = Path.Combine(AppConfig.AppDataDir, "received", offerId.ToString("N")[..8]);
            var roots = await _transport.FetchAsync(owner, offerId, destDir, CancellationToken.None);
            if (roots.Count == 0)
            {
                Balloon("NetClipboard", "Nessun file ricevuto.", ToolTipIcon.Warning);
                return;
            }

            _history.SetMaterialized(item.Id, roots);
            ApplyFiles(roots);
            Balloon("NetClipboard", $"Pronto da incollare · {roots.Count} elemento/i.");
        }
        catch (Exception ex)
        {
            Balloon("NetClipboard", $"Download fallito: {ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void ApplyFiles(IReadOnlyList<string> roots)
    {
        if (_monitor.IsHandleCreated)
            _monitor.BeginInvoke(() => _monitor.ApplyFilesToClipboard(roots));
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    // ----- Comandi UI -----

    private void ShowHistory()
    {
        if (_historyForm.Visible)
            _historyForm.Hide();
        else
            _historyForm.ShowNearCursor();
    }

    private void SendCurrentClipboard()
    {
        if (!_channel.HasKey)
        {
            OpenSettings(false);
            return;
        }
        var payload = _monitor.TryReadClipboard();
        if (payload == null)
            return;
        if (payload.Kind == PayloadKind.Files && payload.Offer != null)
            _offerStore.Register(payload.Offer);
        if (payload.Kind != PayloadKind.Files && ExceedsSize(payload))
        {
            WarnSizeOnce();
            return;
        }
        var peers = _discovery.Peers;
        if (peers.Count == 0)
        {
            _tray.ShowBalloonTip(2000, "NetClipboard", "Nessun dispositivo in linea.", ToolTipIcon.Info);
            return;
        }
        _ = _transport.SendAsync(payload, peers);
        _tray.ShowBalloonTip(1500, "NetClipboard", $"Inviato a {peers.Count} dispositivo/i.", ToolTipIcon.Info);
    }

    private void ToggleSharing()
    {
        _sharingEnabled = !_sharingEnabled;
        _sharingItem.Checked = _sharingEnabled;
        _config.StartSharingEnabled = _sharingEnabled;
        _config.Save();
        UpdateTrayText();
    }

    private void OpenSettings(bool firstRun)
    {
        if (firstRun)
            _tray.ShowBalloonTip(3000, "Benvenuto in NetClipboard",
                "Imposta una password condivisa uguale su tutti i PC.", ToolTipIcon.Info);

        using var form = new SettingsForm(_config);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _channel.SetPassword(_config.Password);
            RestartNetwork();
            UpdateTrayText();
        }
    }

    private void ConfigureFirewall()
    {
        bool ok = FirewallHelper.IsElevated()
            ? FirewallHelper.InstallRulesNow() == 0
            : FirewallHelper.RequestInstallElevated();
        _tray.ShowBalloonTip(2500, "Firewall",
            ok ? "Regola creata correttamente." : "Configurazione annullata o non riuscita.",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void OnPeersChanged()
    {
        if (_monitor.IsHandleCreated)
            _monitor.BeginInvoke(UpdateTrayText);
    }

    private void RefreshDevicesMenu()
    {
        _devicesItem.DropDownItems.Clear();
        var peers = _discovery.Peers.OrderBy(p => p.Name).ToList();
        if (peers.Count == 0)
        {
            _devicesItem.Text = "Dispositivi: nessuno";
            return;
        }
        _devicesItem.Text = $"Dispositivi: {peers.Count}";
        foreach (var p in peers)
            _devicesItem.DropDownItems.Add(new ToolStripMenuItem($"{p.Name}  ·  {p.Address}") { Enabled = false });
    }

    private void UpdateTrayText()
    {
        var count = _discovery.Peers.Count;
        var state = _channel.HasKey ? (_sharingEnabled ? "attiva" : "in pausa") : "nessuna password";
        var text = $"NetClipboard · {state} · {count} device";
        _tray.Text = text.Length > 63 ? text[..63] : text;
        _tray.Icon = IconFactory.Create(_sharingEnabled && _channel.HasKey);
    }

    // ----- Utility -----

    private void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_monitor.IsHandleCreated)
            _monitor.BeginInvoke(() => _tray.ShowBalloonTip(2500, title, text, icon));
    }

    private bool IsShareable(ClipboardPayload p) => p.Kind switch
    {
        PayloadKind.Text => _config.ShareText,
        PayloadKind.Image => _config.ShareImages,
        PayloadKind.Files => _config.ShareFiles,
        _ => false,
    };

    private bool ExceedsSize(ClipboardPayload p) =>
        p.ApproxSize() > _config.MaxTransferMb * 1024L * 1024L;

    private void WarnSizeOnce()
    {
        if (_warnedSize) return;
        _warnedSize = true;
        _tray.ShowBalloonTip(3000, "NetClipboard",
            $"Contenuto oltre il limite di {_config.MaxTransferMb} MB: non inviato.", ToolTipIcon.Warning);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _updateTimer?.Dispose();
        _discovery.Dispose();
        _transport.Dispose();
        _tray.Dispose();
        _historyForm.Dispose();
        _monitor.Dispose();
        ExitThread();
    }
}

/// <summary>Genera al volo un'icona per la tray (verde = attiva, grigia = in pausa).</summary>
internal static class IconFactory
{
    public static Icon Create(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var body = new Rectangle(6, 4, 20, 26);
            using var bodyBrush = new SolidBrush(active ? Color.FromArgb(30, 160, 90) : Color.FromArgb(110, 110, 118));
            g.FillRectangle(bodyBrush, body);

            using var clip = new SolidBrush(Color.FromArgb(230, 230, 235));
            g.FillRectangle(clip, new Rectangle(11, 2, 10, 6));

            using var line = new Pen(Color.White, 2);
            g.DrawLine(line, 10, 13, 22, 13);
            g.DrawLine(line, 10, 18, 22, 18);
            g.DrawLine(line, 10, 23, 18, 23);
        }
        var hicon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hicon).Clone();
    }
}
