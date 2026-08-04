using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;
using NetClipboard.Update;

namespace NetClipboard.Ui;

/// <summary>
/// Cuore dell'app nel system tray. Collega clipboard, cronologia, identità
/// per-dispositivo, scoperta e trasporto sicuro. Nessuna password: la fiducia è
/// per-dispositivo (pairing con codice).
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly DeviceIdentity _identity;
    private readonly TrustStore _trust;
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
    private SettingsForm? _settingsForm;
    private DevicesForm? _devicesForm;
    private bool _sharingEnabled;
    private bool _warnedSize;

    public TrayContext()
    {
        _config = AppConfig.Load();
        _config.Save();

        _identity = DeviceIdentity.LoadOrCreate();
        _trust = new TrustStore();

        Log.Start($"NetClipboard v{Updater.CurrentVersion} · {_config.DisplayName} · " +
                  $"device {DeviceIdentity.ShortFingerprint(_identity.DeviceId)} · porta {_config.Port} · " +
                  $"fidati: {_trust.All.Count}");
        Updater.CleanupOld();

        _offerStore = new OfferStore();
        _history = new ClipboardHistory(_config);
        ClipboardHistory.CleanupReceived(_config.HistoryMaxAgeDays);

        _monitor = new ClipboardMonitor(_config) { OwnerDeviceId = _identity.DeviceId };
        _ = _monitor.Handle;

        _transport = new ClipboardTransport(_config, _identity, _trust, _offerStore)
        {
            PairingConfirm = ShowSasDialog,
        };
        _discovery = new PeerDiscovery(_config, ip => _transport.AddCandidate(ip));
        _historyForm = new HistoryForm(_history);

        _sharingEnabled = _config.StartSharingEnabled;

        var menu = new ContextMenuStrip();
        _sharingItem = new ToolStripMenuItem(L.T("tray.sharing"), null, (_, _) => ToggleSharing()) { Checked = _sharingEnabled };
        menu.Items.Add(_sharingItem);
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.openHistory"), null, (_, _) => ShowHistory()));
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.sendNow"), null, (_, _) => SendCurrentClipboard()));
        menu.Items.Add(new ToolStripSeparator());
        _devicesItem = new ToolStripMenuItem(L.T("tray.devices")) { Enabled = false };
        menu.Items.Add(_devicesItem);
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.devicesAndPairing"), null, (_, _) => OpenDevices()));
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.scan"), null, (_, _) =>
        {
            _transport.ScanOnDemand();
            Balloon(L.T("app.name"), L.T("msg.scanStarted"));
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.settings"), null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.firewall"), null, (_, _) => ConfigureFirewall()));
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.restartNetwork"), null, (_, _) =>
        {
            RestartNetwork();
            Balloon(L.T("app.name"), L.T("msg.networkRestarted"));
        }));
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.openLog"), null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.checkUpdates"), null, (_, _) => _ = CheckForUpdateAsync(true)));
        _updateItem = new ToolStripMenuItem(L.T("tray.installUpdate"), null, (_, _) => InstallPendingUpdate()) { Visible = false };
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L.T("tray.exit"), null, (_, _) => ExitApp()));
        menu.Opening += (_, _) => RefreshDevicesMenu();

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create(_sharingEnabled),
            Text = L.T("app.name"),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowHistory();

        _monitor.ClipboardChanged += OnLocalClipboard;
        _monitor.HistoryHotkeyPressed += ShowHistory;
        _transport.Received += OnRemoteReceived;
        _transport.PeersChanged += OnPeersChanged;
        _trust.Changed += () => { if (_monitor.IsHandleCreated) _monitor.BeginInvoke(UpdateTrayText); };
        _historyForm.ItemChosen += OnHistoryItemChosen;

        UpdateTrayText();
        StartNetwork();

        if (_trust.All.Count == 0)
            _monitor.BeginInvoke(() =>
                _tray.ShowBalloonTip(5000, L.T("welcome.title"), L.T("welcome.body"), ToolTipIcon.Info));

        StartUpdateChecks();
    }

    private void StartNetwork()
    {
        Log.Write("[App] avvio rete (discovery + transport)");
        _discovery.Start();
        _transport.Start();
    }

    private void RestartNetwork()
    {
        _discovery.Stop();
        _transport.Stop();
        StartNetwork();
    }

    // ----- Clipboard locale -> cronologia + push ai fidati -----

    private void OnLocalClipboard(ClipboardPayload payload)
    {
        if (payload.Kind == PayloadKind.Files && payload.Offer != null)
            _offerStore.Register(payload.Offer);

        _history.Add(payload, _config.DisplayName, isLocal: true);

        if (!_sharingEnabled) return;
        if (!IsShareable(payload)) return;
        if (payload.Kind != PayloadKind.Files && ExceedsSize(payload)) { WarnSizeOnce(); return; }

        _ = _transport.SendAsync(payload);
    }

    private void OnRemoteReceived(ReceivedClip clip)
    {
        _monitor.BeginInvoke(() =>
        {
            // Silenzioso di proposito: la clip arriva in cronologia (Win+Alt+V),
            // senza fumetto a ogni copia fatta sull'altro PC.
            _history.Add(clip.Payload, clip.FromName, isLocal: false);
            if (clip.Payload.Kind != PayloadKind.Files && _sharingEnabled)
                _monitor.ApplyToClipboard(clip.Payload);
        });
    }

    // ----- Cronologia -----

    private void OnHistoryItemChosen(HistoryItem item)
    {
        var target = _historyForm.TargetWindow;
        if (item.Kind != PayloadKind.Files)
        {
            var payload = _history.ToPayload(item);
            if (payload == null) { Balloon(L.T("app.name"), L.T("msg.contentGone"), ToolTipIcon.Warning); return; }
            _monitor.BeginInvoke(() => { _monitor.ApplyToClipboard(payload); PasteToTarget(target); });
            return;
        }
        _ = Task.Run(() => MaterializeAsync(item, target));
    }

    /// <summary>Riporta il fuoco alla finestra di origine e simula Ctrl+V (come Win+V).</summary>
    private static void PasteToTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(130);
            NativePaste.SetForegroundWindow(hwnd);
            await Task.Delay(50);
            NativePaste.SendCtrlV();
        });
    }

    private async Task MaterializeAsync(HistoryItem item, IntPtr target)
    {
        TransferForm? ui = null;
        try
        {
            // Già in locale: nessun trasferimento, nessuna finestra, nessun fumetto.
            if (item.LocalRootPaths is { Count: > 0 } && item.LocalRootPaths.All(Exists))
            {
                ApplyFiles(item.LocalRootPaths);
                PasteToTarget(target);
                return;
            }
            if (item.IsLocalOffer) { Balloon(L.T("app.name"), L.T("msg.originalsGone"), ToolTipIcon.Warning); return; }
            if (string.IsNullOrEmpty(item.OwnerId) || string.IsNullOrEmpty(item.OfferId)) return;

            var owner = _transport.Peers.FirstOrDefault(p => p.DeviceId == item.OwnerId && p.Trusted);
            if (owner == null) { Balloon(L.T("app.name"), L.T("msg.ownerOffline", item.OwnerName), ToolTipIcon.Warning); return; }

            var offerId = Guid.Parse(item.OfferId);
            var destDir = Path.Combine(AppConfig.AppDataDir, "received", offerId.ToString("N")[..8]);

            using var cts = new CancellationTokenSource();
            ui = ShowTransfer(item, cts);
            var roots = await _transport.FetchAsync(owner, offerId, destDir, cts.Token, TransferProgress(ui));
            if (roots.Count == 0) { Balloon(L.T("app.name"), L.T("msg.noFiles"), ToolTipIcon.Warning); return; }

            _history.SetMaterialized(item.Id, roots);
            ApplyFiles(roots);
            PasteToTarget(target);
        }
        catch (OperationCanceledException)
        {
            // annullato dall'utente: nessun avviso
        }
        catch (Exception ex)
        {
            Balloon(L.T("app.name"), L.T("msg.downloadFailed", ex.Message), ToolTipIcon.Warning);
        }
        finally
        {
            CloseTransfer(ui);
        }
    }

    // ----- Finestra di avanzamento del trasferimento file -----

    /// <summary>Crea (sul thread UI) la finestrella di avanzamento; compare solo se il download dura.</summary>
    private TransferForm? ShowTransfer(HistoryItem item, CancellationTokenSource cts)
    {
        if (!_monitor.IsHandleCreated) return null;
        return (TransferForm)_monitor.Invoke(new Func<TransferForm>(() =>
        {
            var count = item.FileCount + item.DirCount;
            var f = new TransferForm(
                L.T("transfer.title", item.OwnerName),
                L.T(count == 1 ? "transfer.subtitleOne" : "transfer.subtitleMany",
                    count, ClipboardPayload.HumanSize(item.TotalSize)),
                item.TotalSize, cts);
            f.ShowAfterDelay();
            return f;
        }));
    }

    private static void CloseTransfer(TransferForm? f)
    {
        if (f == null || f.IsDisposed) return;
        try { f.BeginInvoke(f.Finish); } catch { }
    }

    private static IProgress<FetchProgress>? TransferProgress(TransferForm? f) =>
        f == null ? null : new TransferProgressReporter(f);

    /// <summary>Porta l'avanzamento dal thread di rete a quello della UI.</summary>
    private sealed class TransferProgressReporter : IProgress<FetchProgress>
    {
        private readonly TransferForm _form;
        public TransferProgressReporter(TransferForm form) => _form = form;

        public void Report(FetchProgress p)
        {
            if (_form.IsDisposed || !_form.IsHandleCreated) return;
            try { _form.BeginInvoke(() => { if (!_form.IsDisposed) _form.Report(p.CurrentName, p.BytesDone); }); }
            catch { }
        }
    }

    private void ApplyFiles(IReadOnlyList<string> roots)
    {
        if (_monitor.IsHandleCreated) _monitor.BeginInvoke(() => _monitor.ApplyFilesToClipboard(roots));
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    // ----- Pairing / dispositivi -----

    private bool ShowSasDialog(PairingPrompt prompt)
    {
        if (!_monitor.IsHandleCreated) return false;
        return (bool)_monitor.Invoke(new Func<bool>(() =>
        {
            using var dlg = new SasDialog(prompt);
            return dlg.ShowDialog() == DialogResult.OK;
        }));
    }

    private void OpenDevices()
    {
        if (_devicesForm == null || _devicesForm.IsDisposed)
            _devicesForm = new DevicesForm(_identity, _trust, _transport);
        ShowTool(_devicesForm);
    }

    /// <summary>Mostra una finestra-strumento (non modale). La sua X la nasconde nella tray.</summary>
    private static void ShowTool(Form f)
    {
        if (!f.Visible) f.Show();
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        f.Activate();
        f.BringToFront();
    }

    // ----- Comandi UI -----

    private void ShowHistory()
    {
        if (_historyForm.Visible) _historyForm.Hide();
        else _historyForm.ShowNearCursor();
    }

    private void SendCurrentClipboard()
    {
        var payload = _monitor.TryReadClipboard();
        if (payload == null) return;
        if (payload.Kind == PayloadKind.Files && payload.Offer != null) _offerStore.Register(payload.Offer);
        if (payload.Kind != PayloadKind.Files && ExceedsSize(payload)) { WarnSizeOnce(); return; }

        var trusted = _transport.TrustedPeers.Count;
        if (trusted == 0) { Balloon(L.T("app.name"), L.T("msg.noTrustedOnline")); return; }
        _ = _transport.SendAsync(payload);
        Balloon(L.T("app.name"), L.T("msg.sentTo", trusted));
    }

    private void ToggleSharing()
    {
        _sharingEnabled = !_sharingEnabled;
        _sharingItem.Checked = _sharingEnabled;
        _config.StartSharingEnabled = _sharingEnabled;
        _config.Save();
        UpdateTrayText();
    }

    private void OpenSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_config);
            _settingsForm.Saved += () => { RestartNetwork(); UpdateTrayText(); };
        }
        ShowTool(_settingsForm);
    }

    private void ConfigureFirewall()
    {
        bool ok = FirewallHelper.IsElevated() ? FirewallHelper.InstallRulesNow() == 0 : FirewallHelper.RequestInstallElevated();
        Balloon(L.T("firewall.title"), L.T(ok ? "firewall.created" : "firewall.failed"),
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void OnPeersChanged()
    {
        if (_monitor.IsHandleCreated) _monitor.BeginInvoke(UpdateTrayText);
    }

    private void RefreshDevicesMenu()
    {
        _devicesItem.DropDownItems.Clear();
        var peers = _transport.Peers.OrderByDescending(p => p.Trusted).ThenBy(p => p.Name).ToList();
        if (peers.Count == 0) { _devicesItem.Text = L.T("tray.devicesNone"); return; }
        _devicesItem.Text = L.T("tray.devicesCount", peers.Count(p => p.Trusted), peers.Count);
        foreach (var p in peers)
        {
            var mark = p.Trusted ? "🔒 " : "• "; // simboli, non testo da tradurre
            _devicesItem.DropDownItems.Add(
                new ToolStripMenuItem(mark + L.T("tray.peerLine", p.Name, p.Address)) { Enabled = false });
        }
    }

    private void UpdateTrayText()
    {
        var trusted = _transport.TrustedPeers.Count;
        var state = L.T(_sharingEnabled ? "tray.stateActive" : "tray.statePaused");
        var text = L.T("tray.tooltip", state, trusted);
        _tray.Text = text.Length > 63 ? text[..63] : text; // limite di Windows per il tooltip della tray
        _tray.Icon = IconFactory.Create(_sharingEnabled);
    }

    // ----- Auto-update -----

    /// <summary>URL aggiornamenti: override in Impostazioni, altrimenti quello fissato nell'exe.</summary>
    private string UpdateUrl => string.IsNullOrWhiteSpace(_config.UpdateManifestUrl)
        ? Updater.DefaultManifestUrl : _config.UpdateManifestUrl;

    private void StartUpdateChecks()
    {
        if (!_config.AutoUpdateCheck || !Updater.IsConfigured(UpdateUrl)) return;
        _updateTimer = new System.Threading.Timer(_ => _ = CheckForUpdateAsync(false), null,
            TimeSpan.FromSeconds(8), TimeSpan.FromHours(6));
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        var url = UpdateUrl;
        if (!Updater.IsConfigured(url)) { if (manual) Balloon(L.T("update.title"), L.T("update.notConfigured")); return; }
        var info = await Updater.CheckAsync(url, CancellationToken.None);
        if (info == null) { if (manual) Balloon(L.T("update.title"), L.T("update.none")); return; }
        var path = await Updater.DownloadAsync(info, CancellationToken.None);
        if (path == null) { if (manual) Balloon(L.T("update.title"), L.T("update.downloadFailed"), ToolTipIcon.Warning); return; }
        _pendingUpdatePath = path;
        if (_monitor.IsHandleCreated)
            _monitor.BeginInvoke(() =>
            {
                _updateItem.Text = L.T("tray.installUpdateVersion", info.Version);
                _updateItem.Visible = true;
                _tray.ShowBalloonTip(4000, L.T("update.availableTitle"), L.T("update.availableBody", info.Version), ToolTipIcon.Info);
            });
    }

    private void InstallPendingUpdate()
    {
        if (_pendingUpdatePath == null || !File.Exists(_pendingUpdatePath))
        { Balloon(L.T("update.title"), L.T("update.nonePending"), ToolTipIcon.Warning); return; }
        if (Updater.ApplyAndRestart(_pendingUpdatePath))
        {
            _tray.Visible = false;
            _updateTimer?.Dispose();
            _discovery.Dispose(); _transport.Dispose(); _tray.Dispose();
            ExitThread();
        }
        else Balloon(L.T("update.title"), L.T("update.installFailed"), ToolTipIcon.Warning);
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(Log.FilePath)) Log.Write("(log)");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { Balloon(L.T("app.name"), L.T("msg.logOpenFailed", ex.Message), ToolTipIcon.Warning); }
    }

    // ----- Utility -----

    private void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_monitor.IsHandleCreated) _monitor.BeginInvoke(() => _tray.ShowBalloonTip(2500, title, text, icon));
    }

    private bool IsShareable(ClipboardPayload p) => p.Kind switch
    {
        PayloadKind.Text => _config.ShareText,
        PayloadKind.Image => _config.ShareImages,
        PayloadKind.Files => _config.ShareFiles,
        _ => false,
    };

    private bool ExceedsSize(ClipboardPayload p) => p.ApproxSize() > _config.MaxTransferMb * 1024L * 1024L;

    private void WarnSizeOnce()
    {
        if (_warnedSize) return;
        _warnedSize = true;
        Balloon(L.T("app.name"), L.T("msg.tooBig", _config.MaxTransferMb), ToolTipIcon.Warning);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _updateTimer?.Dispose();
        _settingsForm?.Dispose();
        _devicesForm?.Dispose();
        _discovery.Dispose();
        _transport.Dispose();
        _tray.Dispose();
        _historyForm.Dispose();
        _monitor.Dispose();
        _identity.Dispose();
        ExitThread();
    }
}

/// <summary>Icona del brand (appicon.ico embedded), usata per tray e finestre.</summary>
internal static class IconFactory
{
    private static Icon? _cached;

    public static Icon Create(bool active) => Shared;

    public static Icon Shared
    {
        get
        {
            if (_cached != null)
                return _cached;
            try
            {
                using var s = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("NetClipboard.appicon.ico");
                _cached = s != null ? new Icon(s) : SystemIcons.Application;
            }
            catch
            {
                _cached = SystemIcons.Application;
            }
            return _cached;
        }
    }
}
