using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Droid.Platform;
using NetClipboard.Net;

// I nomi di Avalonia e quelli dei widget di Android si sovrappongono: qui si
// disegna con Avalonia, e lo si dice una volta sola.
using Button = Avalonia.Controls.Button;
using Color = Avalonia.Media.Color;

namespace NetClipboard.Droid.Views;

/// <summary>
/// La schermata: chi siamo, chi c'e' in rete, e i due gesti che servono —
/// accoppiare un dispositivo e mandargli quello che si ha in clipboard.
///
/// Non tiene stato proprio: legge da <see cref="NetClipboardHost.Current"/>, che
/// vive nel servizio. Se il servizio non e' ancora partito la schermata mostra un
/// elenco vuoto e si aggiorna da sola quando arriva.
/// </summary>
public sealed class MainView : UserControl
{
    private readonly TextBlock _self = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Opacity = 0.75 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _peers = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _pair = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _send = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly Border _promptLayer;
    private Border _promptCard = null!;
    private readonly TextBlock _promptTitle = new() { FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _promptBody = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Button _promptAccept = new();
    private readonly Button _promptReject = new();
    private TaskCompletionSource<bool?>? _promptAnswer;

    private List<Peer> _shown = new();
    private readonly DispatcherTimer _refresh;

    public MainView()
    {
        _pair.Content = L.T("mobile.pair");
        _pair.Click += (_, _) => _ = PairSelectedAsync();
        _send.Content = L.T("mobile.sendClipboard");
        _send.Click += (_, _) => _ = SendClipboardAsync();

        var buttons = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_send);
        buttons.Children.Add(_pair);

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock { Text = L.T("app.name"), FontSize = 22, FontWeight = FontWeight.SemiBold });
        header.Children.Add(_self);
        header.Children.Add(_status);

        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(buttons);
        dock.Children.Add(_peers);

        _promptLayer = BuildPromptLayer();

        var root = new Panel();
        root.Children.Add(dock);
        root.Children.Add(_promptLayer);
        Content = root;

        // Le presenze durano quindici secondi e il giro di ping e' ogni tre: due
        // secondi sono abbastanza per non far sembrare l'elenco fermo, e poco
        // abbastanza da non ridisegnare per niente.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refresh.Tick += (_, _) => Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Prompts.Handler = ShowPromptAsync;
        Hook(NetClipboardHost.Current);
        _refresh.Start();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refresh.Stop();
        // Da qui in poi non c'e' piu' nessuno a cui chiedere: le conferme
        // torneranno "nessuna risposta", che e' la verita'.
        Prompts.Handler = null;
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

    private void OnReceived(ReceivedClip clip)
    {
        // Negli appunti ci ha gia' pensato il servizio (vale anche ad app
        // chiusa): qui resta solo da dirlo a chi sta guardando.
        Dispatcher.UIThread.Post(() => _status.Text = L.T("mobile.received", clip.FromName));
    }

    private void Refresh()
    {
        var host = NetClipboardHost.Current;
        Hook(host);

        if (host == null)
        {
            _self.Text = L.T("mobile.starting");
            return;
        }

        _self.Text = L.T("mobile.selfLine",
            host.Config.DisplayName,
            DeviceIdentity.ShortFingerprint(host.Identity.DeviceId));

        var selected = SelectedPeer()?.DeviceId;
        _shown = host.Peers.OrderByDescending(p => p.Trusted).ThenBy(p => p.Label, StringComparer.CurrentCulture).ToList();
        _peers.ItemsSource = _shown
            .Select(p => $"{p.Label}  ·  {(p.Trusted ? L.T("mobile.peerTrusted") : L.T("mobile.peerUnknown"))}\n{p.Address}")
            .ToList();

        var again = _shown.FindIndex(p => p.DeviceId == selected);
        _peers.SelectedIndex = again;

        if (_shown.Count == 0) _self.Text += "\n" + L.T("mobile.noPeers");
    }

    private Peer? SelectedPeer()
    {
        var i = _peers.SelectedIndex;
        return i >= 0 && i < _shown.Count ? _shown[i] : null;
    }

    // ----- i due gesti -----

    private async Task PairSelectedAsync()
    {
        var host = NetClipboardHost.Current;
        var peer = SelectedPeer();
        if (host == null || peer == null) { _status.Text = L.T("mobile.pickOne"); return; }

        _status.Text = L.T("mobile.pairing", peer.Label);
        var (outcome, _) = await host.PairAsync(peer);
        _status.Text = outcome switch
        {
            PairOutcome.Paired => L.T("mobile.pairDone", peer.Label),
            PairOutcome.Rejected => L.T("mobile.pairRejected"),
            _ => L.T("mobile.pairFailed"),
        };
        Refresh();
    }

    private async Task SendClipboardAsync()
    {
        var host = NetClipboardHost.Current;
        if (host == null) return;

        // Si legge qui, mentre l'applicazione e' in primo piano: e' l'unico
        // momento in cui Android lo consente (vedi AndroidClipboard).
        var payload = AndroidClipboard.Read();
        if (payload == null) { _status.Text = L.T("mobile.clipboardEmpty"); return; }

        var trusted = host.Peers.Count(p => p.Trusted);
        if (trusted == 0) { _status.Text = L.T("msg.noTrustedOnline"); return; }

        await host.SendAsync(payload);
        _status.Text = L.T("msg.sentTo", trusted);
    }

    // ----- la domanda a schermo -----

    private Border BuildPromptLayer()
    {
        _promptAccept.HorizontalAlignment = HorizontalAlignment.Stretch;
        _promptReject.HorizontalAlignment = HorizontalAlignment.Stretch;
        _promptAccept.Click += (_, _) => Answer(true);
        _promptReject.Click += (_, _) => Answer(false);

        var box = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        box.Children.Add(_promptTitle);
        box.Children.Add(_promptBody);
        box.Children.Add(_promptReject); // il rifiuto sta per primo, di proposito
        box.Children.Add(_promptAccept);

        _promptCard = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(12),
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

    /// <summary>
    /// Colora la card della domanda seguendo il tema di sistema.
    ///
    /// Non è un vezzo: era bianca fissa, e su un telefono in tema scuro il
    /// risultato era una superficie bianca e vuota. Il testo di Fluent in tema
    /// scuro è bianco, e lo sfondo dei pulsanti è un bianco appena accennato —
    /// su una card bianca sparivano tutti e due insieme, senza alcun errore.
    ///
    /// Due colori dichiarati e la scelta lasciata al sistema: è lo stesso modo in
    /// cui l'applicazione Windows tiene la sua palette (<c>Ui/Theme.cs</c>). Lo
    /// sfondo si dichiara, il colore del testo no: se la card è del colore che il
    /// tema si aspetta, il testo che il tema disegna sopra è già quello giusto.
    /// </summary>
    private void PaintPromptCard()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        _promptCard.Background = dark
            ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E))
            : Brushes.White;
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
            PaintPromptCard();
            _promptTitle.Text = request.Title;
            _promptBody.Text = request.Body;
            _promptAccept.Content = request.Accept;
            _promptReject.Content = request.Reject;
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
}
