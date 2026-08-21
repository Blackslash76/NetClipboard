using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Droid.Platform;
using NetClipboard.Net;

// I nomi di Avalonia e quelli dei widget di Android si sovrappongono: qui si
// disegna con Avalonia, e lo si dice una volta sola.
using Color = Avalonia.Media.Color;
using Orientation = Avalonia.Layout.Orientation;

namespace NetClipboard.Droid.Views;

/// <summary>
/// La schermata dell'applicazione, e da questa versione e' soprattutto la
/// CRONOLOGIA: l'elenco di cio' che e' passato per la clipboard condivisa, da
/// cui si sceglie che cosa portare negli appunti del telefono.
///
/// E' il gemello del pannello Win+V del PC — stessa tavolozza, stesse schede,
/// stesso distintivo per tipo di contenuto, stesso anello di scadenza — e serve
/// allo stesso scopo: siccome qui gli appunti NON si sovrascrivono da soli (vedi
/// <see cref="NetClipboardHost"/>), ci vuole un posto dove ritrovare cio' che e'
/// arrivato. Questo e' quel posto.
///
/// I dispositivi in rete stanno nella seconda scheda: si guardano quando si
/// accoppia qualcosa, cioe' raramente, e non devono occupare la schermata che si
/// apre venti volte al giorno.
///
/// Non tiene stato proprio: legge da <see cref="NetClipboardHost.Current"/>, che
/// vive nel servizio. Se il servizio non e' ancora partito la schermata mostra
/// elenchi vuoti e si aggiorna da sola quando arriva.
/// </summary>
public sealed class MainView : UserControl
{
    private enum Tab { History, Devices }

    // ----- intestazione -----
    private readonly Border _mark = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(5) };
    private readonly TextBlock _appName = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _self = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _online = new() { FontSize = 12, FontWeight = FontWeight.SemiBold };
    private Border _header = null!;

    // ----- schede -----
    private readonly TabButton _tabHistory = new(L.T("mobile.tabHistory"));
    private readonly TabButton _tabDevices = new(L.T("mobile.tabDevices"));
    private Border _tabStrip = null!;
    private Tab _tab = Tab.History;

    // ----- elenchi -----
    private readonly StackPanel _historyList = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly StackPanel _deviceList = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _historyEmpty = Empty();
    private readonly TextBlock _deviceEmpty = Empty();
    private ScrollViewer _historyPane = null!;
    private ScrollViewer _devicePane = null!;

    /// <summary>Firme del contenuto mostrato: si ricostruisce solo quando cambia davvero.</summary>
    private string _historySignature = "";
    private string _deviceSignature = "";

    // ----- barra in fondo -----
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, IsVisible = false };
    private readonly PillButton _send = new(L.T("mobile.sendClipboard"), filled: true);
    private Border _actions = null!;

    // ----- sovrapposizioni -----
    private readonly Border _promptLayer;
    private Border _promptCard = null!;
    private readonly TextBlock _promptTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _promptBody = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _promptCode = new()
    {
        FontSize = 34,
        FontWeight = FontWeight.Bold,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 14, 0, 6),
        IsVisible = false,
    };
    private readonly PillButton _promptAccept = new("", filled: true);
    private readonly PillButton _promptReject = new("");
    private TaskCompletionSource<bool?>? _promptAnswer;

    private readonly Border _sheetLayer;
    private Border _sheetCard = null!;
    private readonly TextBlock _sheetTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly PillButton _sheetPin = new("");
    private readonly PillButton _sheetDelete = new(L.T("common.delete"));
    private readonly PillButton _sheetClose = new(L.T("common.cancel"));
    private HistoryItem? _sheetItem;

    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _countdown;

    public MainView()
    {
        _send.Click += () => _ = SendClipboardAsync();

        _tabHistory.Click += () => Show(Tab.History);
        _tabDevices.Click += () => Show(Tab.Devices);
        _tabHistory.Selected = true;

        _promptLayer = BuildPromptLayer();
        _sheetLayer = BuildSheetLayer();

        var root = new Panel();
        root.Children.Add(BuildMain());
        root.Children.Add(_sheetLayer);
        root.Children.Add(_promptLayer);
        Content = root;

        // Le presenze durano quindici secondi e il giro di ping e' ogni tre: due
        // secondi sono abbastanza per non far sembrare l'elenco fermo, e poco
        // abbastanza da non ridisegnare per niente.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick += (_, _) => Refresh();

        // L'anello di scadenza si consuma al secondo: qui si ridisegnano solo le
        // righe che ce l'hanno, e il battito si ferma da solo quando non ce ne
        // sono piu'.
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => TickExpiry();

        ActualThemeVariantChanged += (_, _) => ApplyPalette();
    }

    // ----- costruzione -----

    private Control BuildMain()
    {
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(_appName);
        identity.Children.Add(_self);

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _mark.VerticalAlignment = VerticalAlignment.Center;
        _mark.Margin = new Thickness(0, 0, 10, 0);
        _online.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_mark, 0);
        Grid.SetColumn(identity, 1);
        Grid.SetColumn(_online, 2);
        headerGrid.Children.Add(_mark);
        headerGrid.Children.Add(identity);
        headerGrid.Children.Add(_online);

        _header = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid,
        };

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tabs.Children.Add(_tabHistory);
        tabs.Children.Add(_tabDevices);
        _tabStrip = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tabs,
        };

        _historyPane = new ScrollViewer { Content = _historyList };
        _devicePane = new ScrollViewer { Content = _deviceList, IsVisible = false };

        var content = new Panel();
        content.Children.Add(_historyPane);
        content.Children.Add(_devicePane);
        content.Children.Add(_historyEmpty);
        content.Children.Add(_deviceEmpty);

        var actionStack = new StackPanel { Spacing = 10 };
        actionStack.Children.Add(_status);
        actionStack.Children.Add(_send);
        _actions = new Border
        {
            Padding = new Thickness(12, 10, 12, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = actionStack,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_tabStrip, Dock.Top);
        DockPanel.SetDock(_actions, Dock.Bottom);
        dock.Children.Add(_header);
        dock.Children.Add(_tabStrip);
        dock.Children.Add(_actions);
        dock.Children.Add(content);
        return dock;
    }

    private static TextBlock Empty() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 13.5,
        MaxWidth = 320,
        Margin = new Thickness(24),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsVisible = false,
    };

    // ----- ciclo di vita -----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Prompts.Handler = ShowPromptAsync;
        Palette.Changed += OnPaletteChanged;
        if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested += OnBackRequested;
        Hook(NetClipboardHost.Current);
        ApplyPalette();
        NetClipboardHost.Current?.History.PurgeExpired();
        _refresh.Start();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refresh.Stop();
        _countdown.Stop();
        Palette.Changed -= OnPaletteChanged;
        // Da qui in poi non c'e' piu' nessuno a cui chiedere: le conferme
        // torneranno "nessuna risposta", che e' la verita'.
        Prompts.Handler = null;
        if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested -= OnBackRequested;
        Unhook(NetClipboardHost.Current);
        base.OnDetachedFromVisualTree(e);
    }

    private NetClipboardHost? _hooked;

    private void Hook(NetClipboardHost? host)
    {
        if (host == null || ReferenceEquals(host, _hooked)) return;
        Unhook(_hooked);
        host.Received += OnReceived;
        host.PeersChanged += OnPeersChanged;
        _hooked = host;
    }

    private void Unhook(NetClipboardHost? host)
    {
        if (host == null) return;
        host.Received -= OnReceived;
        host.PeersChanged -= OnPeersChanged;
        if (ReferenceEquals(host, _hooked)) _hooked = null;
    }

    private void OnPeersChanged() => Dispatcher.UIThread.Post(Refresh);

    private void OnReceived(ReceivedClip clip, HistoryItem item)
    {
        // Non si tocca la clipboard: il contenuto e' in cronologia e lo propone la
        // notifica. Qui si aggiorna l'elenco e si dice da chi e' arrivato.
        Dispatcher.UIThread.Post(() =>
        {
            Say(L.T("mobile.received", clip.FromName));
            Refresh();
        });
    }

    // ----- tavolozza -----

    private void OnPaletteChanged() => Dispatcher.UIThread.Post(Repaint);

    private void ApplyPalette()
    {
        // Se il tema e' davvero cambiato, Use() avvisa e Repaint arriva da li';
        // se non e' cambiato niente (primo aggancio) si dipinge comunque.
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        if (dark == Palette.Dark) Repaint();
        else Palette.Use(dark);
    }

    private void Repaint()
    {
        Background = Palette.Brush(Palette.Bg);
        _header.Background = Palette.Brush(Palette.HeaderBg);
        _header.BorderBrush = Palette.Brush(Palette.Divider);
        _tabStrip.Background = Palette.Brush(Palette.HeaderBg);
        _tabStrip.BorderBrush = Palette.Brush(Palette.Divider);
        _actions.Background = Palette.Brush(Palette.HeaderBg);
        _actions.BorderBrush = Palette.Brush(Palette.Divider);

        _mark.Background = Palette.Diagonal(Palette.Accent, Palette.AccentAlt);
        _appName.Foreground = Palette.Brush(Palette.TextMain);
        _self.Foreground = Palette.Brush(Palette.TextMuted);
        _status.Foreground = Palette.Brush(Palette.TextMuted);
        _historyEmpty.Foreground = Palette.Brush(Palette.TextMuted);
        _deviceEmpty.Foreground = Palette.Brush(Palette.TextMuted);

        _promptCard.Background = Palette.Brush(Palette.Card);
        _promptTitle.Foreground = Palette.Brush(Palette.TextMain);
        _promptBody.Foreground = Palette.Brush(Palette.TextMuted);
        _promptCode.Foreground = Palette.Brush(Palette.TextMain);
        _sheetCard.Background = Palette.Brush(Palette.Card);
        _sheetTitle.Foreground = Palette.Brush(Palette.TextMain);

        foreach (var visual in _historyList.Children) visual.InvalidateVisual();
        foreach (var visual in _deviceList.Children) visual.InvalidateVisual();
        _tabHistory.InvalidateVisual();
        _tabDevices.InvalidateVisual();
        _send.InvalidateVisual();
        InvalidateVisual();
    }

    // ----- schede -----

    private void Show(Tab tab)
    {
        _tab = tab;
        _tabHistory.Selected = tab == Tab.History;
        _tabDevices.Selected = tab == Tab.Devices;
        _historyPane.IsVisible = tab == Tab.History;
        _devicePane.IsVisible = tab == Tab.Devices;
        if (tab == Tab.History) NetClipboardHost.Current?.History.PurgeExpired();
        Refresh();
    }

    // ----- aggiornamento -----

    private void Refresh()
    {
        var host = NetClipboardHost.Current;
        Hook(host);

        if (host == null)
        {
            _appName.Text = L.T("app.name");
            _self.Text = L.T("mobile.starting");
            _online.Text = "";
            return;
        }

        _appName.Text = L.T("app.name");
        _self.Text = L.T("mobile.selfLine",
            host.Config.DisplayName,
            DeviceIdentity.ShortFingerprint(host.Identity.DeviceId));

        var peers = host.Peers
            .OrderByDescending(p => p.Trusted)
            .ThenBy(p => p.Label, StringComparer.CurrentCulture)
            .ToList();

        var trusted = peers.Count(p => p.Trusted);
        _online.Text = trusted > 0 ? L.T("mobile.onlineTrusted", trusted)
                     : peers.Count > 0 ? L.T("mobile.onlineUnpaired", peers.Count)
                     : L.T("mobile.onlineNone");
        _online.Foreground = Palette.Brush(trusted > 0 ? Palette.Success
                                         : peers.Count > 0 ? Palette.Warn : Palette.TextMuted);
        _send.IsEnabled = trusted > 0;

        RefreshDevices(peers);
        RefreshHistory(host);
    }

    private void RefreshDevices(List<Peer> peers)
    {
        var signature = string.Join("|", peers.Select(p => $"{p.DeviceId}:{p.Trusted}:{p.Address}:{p.Label}"));
        if (signature != _deviceSignature)
        {
            _deviceSignature = signature;
            _deviceList.Children.Clear();
            foreach (var peer in peers)
            {
                var row = new PeerRowView(peer);
                row.PairRequested += p => _ = PairAsync(row, p);
                _deviceList.Children.Add(row);
            }
        }

        _deviceEmpty.Text = L.T("mobile.noPeers");
        _deviceEmpty.IsVisible = _tab == Tab.Devices && peers.Count == 0;
    }

    private void RefreshHistory(NetClipboardHost host)
    {
        var items = host.History.Items
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.TimestampUtc)
            .ToList();

        // Nella firma entra anche cio' che cambia l'aspetto della riga senza
        // cambiare l'elenco — il pin, il gia' usato — altrimenti si toccherebbe
        // il pin e non si vedrebbe niente fino al prossimo arrivo.
        var signature = string.Join("|", items.Select(i => $"{i.Id}:{i.Pinned}:{i.Used}"));
        if (signature != _historySignature)
        {
            _historySignature = signature;
            _historyList.Children.Clear();
            foreach (var item in items)
            {
                var row = new HistoryRowView(item, host.History);
                row.Chosen += Choose;
                row.OptionsRequested += ShowSheet;
                _historyList.Children.Add(row);
            }
        }

        _historyEmpty.Text = L.T("mobile.historyEmpty");
        _historyEmpty.IsVisible = _tab == Tab.History && items.Count == 0;

        // Il battito serve solo se c'e' un anello che si consuma.
        var live = items.Any(i => i.FromExternal && !ClipboardHistory.IsSpent(i));
        if (live && _tab == Tab.History) _countdown.Start();
        else _countdown.Stop();
    }

    private void TickExpiry()
    {
        var stillLive = false;
        foreach (var child in _historyList.Children)
        {
            if (child is not HistoryRowView row || !row.Item.FromExternal) continue;
            row.InvalidateVisual();
            if (!ClipboardHistory.IsSpent(row.Item)) stillLive = true;
        }
        if (!stillLive) _countdown.Stop();
    }

    // ----- i gesti -----

    /// <summary>
    /// Il tocco su una riga: e' il gesto deliberato che sostituisce la
    /// sovrascrittura automatica degli appunti.
    /// </summary>
    private void Choose(HistoryItem item)
    {
        var host = NetClipboardHost.Current;
        if (host == null) return;

        if (ClipboardHistory.IsSpent(item))
        {
            Say(L.T(item.Used ? "history.used" : "history.expired"));
            return;
        }

        // I file non sono un contenuto, sono un'offerta: sul filo e' viaggiato
        // solo l'elenco, e i byte si vanno a prendere adesso.
        if (item.Kind == PayloadKind.Files)
        {
            _ = MaterializeAsync(item);
            return;
        }

        Say(host.PutInClipboard(item.Id) ? L.T("mobile.copied") : L.T("mobile.notCopyable"));
    }

    /// <summary>Voce il cui prelievo e' in corso: un tocco per volta, o si scarica due volte.</summary>
    private string? _fetching;

    private async Task MaterializeAsync(HistoryItem item)
    {
        var host = NetClipboardHost.Current;
        if (host == null || _fetching != null) return;

        _fetching = item.Id;
        Say(L.T("mobile.fetching", item.Origin));

        // Il prelievo puo' durare: si racconta mentre va, altrimenti sembra
        // piantato. Il rapporto arriva da un thread della rete, e Progress<T>
        // creato qui lo riporta da solo sul thread dell'interfaccia.
        var progress = new Progress<FetchProgress>(p =>
            Say(L.T("mobile.fetchProgress", p.CurrentName, p.FilesDone, item.FileCount)));

        try
        {
            var saved = await host.MaterializeAsync(item, progress);
            Say(saved switch
            {
                0 => L.T("msg.noFiles"),
                1 => L.T("mobile.savedOne"),
                _ => L.T("mobile.savedToDownloads", saved),
            });
        }
        catch (OperationCanceledException)
        {
            Say(L.T("mobile.fetchCancelled"));
        }
        catch (Exception ex)
        {
            Say(L.T("msg.downloadFailed", ex.Message));
        }
        finally
        {
            _fetching = null;
            _historySignature = ""; // la voce ora e' usata: la riga deve ridisegnarsi
            Refresh();
        }
    }

    private async Task PairAsync(PeerRowView row, Peer peer)
    {
        Say(L.T("mobile.pairing", peer.Label));
        var (outcome, _) = await NetClipboardHost.Current!.PairAsync(peer);
        row.Busy = false;
        Say(outcome switch
        {
            PairOutcome.Paired => L.T("mobile.pairDone", peer.Label),
            PairOutcome.Rejected => L.T("mobile.pairRejected"),
            _ => L.T("mobile.pairFailed"),
        });
        _deviceSignature = ""; // la riga deve ridisegnarsi con lo stato nuovo
        Refresh();
    }

    private async Task SendClipboardAsync()
    {
        var host = NetClipboardHost.Current;
        if (host == null) return;

        // Si legge qui, mentre l'applicazione e' in primo piano: e' l'unico
        // momento in cui Android lo consente (vedi AndroidClipboard).
        var payload = AndroidClipboard.Read();
        if (payload == null) { Say(L.T("mobile.clipboardEmpty")); return; }

        var trusted = host.Peers.Count(p => p.Trusted);
        if (trusted == 0) { Say(L.T("msg.noTrustedOnline")); return; }

        await host.SendAsync(payload);
        Say(L.T("msg.sentTo", trusted));
        Refresh();
    }

    private void Say(string message)
    {
        _status.Text = message;
        _status.IsVisible = true;
    }

    // ----- le azioni su una voce -----

    private Border BuildSheetLayer()
    {
        _sheetPin.Click += () =>
        {
            var host = NetClipboardHost.Current;
            if (host != null && _sheetItem != null) host.History.TogglePin(_sheetItem.Id);
            HideSheet();
            Refresh();
        };
        _sheetDelete.Click += () =>
        {
            var host = NetClipboardHost.Current;
            if (host != null && _sheetItem != null) host.History.Remove(_sheetItem.Id);
            HideSheet();
            Refresh();
        };
        _sheetClose.Click += HideSheet;

        // In fondo piu' spazio che in alto: sotto ci passa la barra dei gesti di
        // Android, e un pulsante che le finisce addosso si preme per sbaglio.
        var box = new StackPanel { Spacing = 8, Margin = new Thickness(16, 14, 16, 34) };
        box.Children.Add(_sheetTitle);
        box.Children.Add(_sheetPin);
        box.Children.Add(_sheetDelete);
        box.Children.Add(_sheetClose);

        _sheetCard = new Border
        {
            CornerRadius = new CornerRadius(16, 16, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = box,
        };

        var layer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)),
            IsVisible = false,
            Child = _sheetCard,
        };
        // Toccare fuori dalla scheda chiude: e' cio' che si prova a fare per
        // primo, e su Android e' cio' che fa qualunque altro foglio.
        layer.Tapped += (_, e) => { if (ReferenceEquals(e.Source, layer)) HideSheet(); };
        return layer;
    }

    private void ShowSheet(HistoryItem item)
    {
        _sheetItem = item;
        _sheetTitle.Text = item.Preview.Replace('\r', ' ').Replace('\n', ' ');
        _sheetPin.Text = L.T(item.Pinned ? "history.unpin" : "history.pin");
        _sheetLayer.IsVisible = true;
    }

    private void HideSheet()
    {
        _sheetLayer.IsVisible = false;
        _sheetItem = null;
    }

    // ----- la domanda a schermo -----

    private Border BuildPromptLayer()
    {
        _promptAccept.Tint = () => Palette.Primary;
        _promptAccept.Click += () => Answer(true);
        _promptReject.Click += () => Answer(false);

        var box = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        box.Children.Add(_promptTitle);
        box.Children.Add(_promptBody);
        box.Children.Add(_promptCode);
        box.Children.Add(_promptReject); // il rifiuto sta per primo, di proposito
        box.Children.Add(_promptAccept);

        _promptCard = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { Content = box },
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            IsVisible = false,
            Child = _promptCard,
        };
    }

    private Task<bool?> ShowPromptAsync(PromptRequest request, CancellationToken giveUp)
    {
        // Chi chiama sta su un thread del trasporto e sta aspettando: qui si passa
        // al thread dell'interfaccia e si restituisce una promessa.
        var tcs = new TaskCompletionSource<bool?>();

        // L'altro dispositivo ha annullato: la domanda sparisce da sola. Senza
        // questo resterebbe a schermo fino alla scadenza, chiedendo di
        // confrontare un codice che dall'altra parte non esiste piu'.
        giveUp.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_promptAnswer, tcs)) HidePrompt();
            tcs.TrySetResult(null);
        }));

        Dispatcher.UIThread.Post(() =>
        {
            if (_promptAnswer != null)
            {
                // Una domanda per volta. Il core lo garantisce gia' per conto suo,
                // ma se cambiasse idea, meglio un no che due finestre sovrapposte.
                tcs.TrySetResult(null);
                return;
            }
            _promptAnswer = tcs;
            // A ogni comparsa, non una volta sola: il tema di sistema può cambiare
            // mentre l'applicazione è aperta.
            ApplyPalette();
            _promptTitle.Text = request.Title;
            _promptBody.Text = request.Body;
            // Il codice di accoppiamento si legge a voce da una parte e si
            // confronta dall'altra: va staccato dal testo e distanziato, come nel
            // dialogo del PC, altrimenti sei cifre di seguito si sbagliano.
            _promptCode.Text = request.Code == null ? "" : string.Join("  ", request.Code.ToCharArray());
            _promptCode.IsVisible = request.Code != null;
            _promptAccept.Text = request.Accept;
            _promptReject.Text = request.Reject;
            _promptLayer.IsVisible = true;
        });
        return tcs.Task;
    }

    private void Answer(bool yes)
    {
        var pending = _promptAnswer;
        HidePrompt();
        pending?.TrySetResult(yes);
    }

    private void HidePrompt()
    {
        _promptLayer.IsVisible = false;
        _promptAnswer = null;
    }

    /// <summary>
    /// Il tasto indietro di Android chiude cio' che e' aperto, dal piu' interno
    /// al piu' esterno, e solo alla fine esce dall'applicazione.
    ///
    /// Una domanda tolta con l'indietro si chiude con "nessuna risposta", non con
    /// un no: per l'accoppiamento e per gli invii equivale a un rifiuto — che e'
    /// giusto, chi non ha confrontato il codice non deve accettare — ma una
    /// presentazione verra' riproposta piu' tardi invece che rifiutata per sempre.
    /// La differenza la conosce il core; qui basta non fingere una risposta che
    /// nessuno ha dato.
    /// </summary>
    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_sheetLayer.IsVisible) { HideSheet(); e.Handled = true; return; }
        if (_promptLayer.IsVisible)
        {
            var pending = _promptAnswer;
            HidePrompt();
            pending?.TrySetResult(null);
            e.Handled = true;
            return;
        }
        if (_tab != Tab.History) { Show(Tab.History); e.Handled = true; }
    }

    /// <summary>
    /// Una linguetta della barra delle schede. Disegnata come tutto il resto,
    /// cosi' il selezionato usa gli stessi due colori delle righe selezionate del
    /// pannello di Windows.
    /// </summary>
    private sealed class TabButton : Control
    {
        private string _text;
        private bool _selected;

        public event Action? Click;

        public TabButton(string text)
        {
            _text = text;
            Height = 34;
            Tapped += (_, _) => Click?.Invoke();
        }

        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; InvalidateVisual(); }
        }

        public string Text
        {
            get => _text;
            set { if (_text == value) return; _text = value; InvalidateMeasure(); InvalidateVisual(); }
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(Ink.Lay(_text, 14, FontWeight.SemiBold, Palette.TextMain).Width + 32, 34);

        public override void Render(DrawingContext ctx)
        {
            var box = new RoundedRect(new Rect(Bounds.Size), 17);
            if (_selected) ctx.DrawRectangle(Palette.Brush(Palette.Sel), null, box);
            Ink.Draw(ctx, _text, 14, FontWeight.SemiBold,
                _selected ? Palette.Accent : Palette.TextMuted, new Rect(Bounds.Size), center: true);
        }
    }
}
