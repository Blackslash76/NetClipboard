using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using NetClipboard.Core;
using NetClipboard.Droid.Platform;
using NetClipboard.Net;

namespace NetClipboard.Droid.Services;

/// <summary>
/// Il servizio in primo piano che tiene NetClipboard in ascolto sulla rete.
///
/// Su Android un'applicazione che non e' in primo piano viene messa a dormire, e
/// un ascoltatore addormentato non riceve niente: per restare raggiungibile
/// serve un servizio dichiarato, con la sua notifica sempre visibile. La notifica
/// non e' una seccatura da nascondere — e' il modo in cui il sistema dice a chi
/// usa il telefono che qualcosa sta ascoltando la rete per suo conto, e va bene
/// che si veda.
///
/// <para><b>Perche' il tipo e' "connectedDevice" e non "dataSync".</b> Da Android
/// 15 un servizio di tipo <c>dataSync</c> puo' stare in piedi al massimo sei ore
/// ogni ventiquattro: dopo, il sistema lo ferma. Una clipboard condivisa che
/// smette di funzionare nel pomeriggio sarebbe peggio di una che non c'e'.
/// <c>connectedDevice</c> descrive esattamente cio' che facciamo — parlare con
/// altri apparecchi sulla stessa rete — e non ha quel tetto. Il permesso che lo
/// qualifica e' <c>CHANGE_WIFI_MULTICAST_STATE</c>, che ci serve comunque.</para>
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class NetClipboardService : Service
{
    public const string ActionStop = "it.eulogic.netclipboard.STOP";

    /// <summary>Metti negli appunti la voce indicata da <see cref="ExtraItemId"/>.</summary>
    public const string ActionPaste = "it.eulogic.netclipboard.PASTE";

    public const string ExtraItemId = "itemId";

    private const int NotificationId = 4501;
    private const string ChannelId = "netclipboard.status";

    /// <summary>
    /// Canale separato per gli arrivi: quello del servizio e' a importanza bassa
    /// perche' non deve farsi notare, questo invece deve. Tenerli insieme
    /// significherebbe o una notifica di stato che suona, o un arrivo che non si
    /// vede.
    /// </summary>
    private const string ArrivalChannelId = "netclipboard.arrivals";

    private WifiManager.MulticastLock? _multicast;
    private NetClipboardHost? _host;

    /// <summary>Il servizio non si lega a nessuno: l'interfaccia legge da <see cref="NetClipboardHost.Current"/>.</summary>
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // L'avvio viene PRIMA di qualunque azione: se il sistema ci avesse
        // fermati, l'azione della notifica arriverebbe con la cronologia non
        // ancora caricata e non troverebbe niente da mettere negli appunti.
        if (_host == null)
        {
            ShowNotification();
            AcquireLocks();
            _host = new NetClipboardHost(
                FilesDir?.AbsolutePath ?? CacheDir!.AbsolutePath,
                DeviceName());
            _host.Received += OnArrived;
            _host.PeersChanged += OnPeersChanged;
            _host.Start();
        }

        if (intent?.Action == ActionPaste)
            PasteFromNotification(intent);

        // Sticky: se il sistema ci ferma per far posto a qualcos'altro, ci
        // rifa' partire da solo. E' cio' che si vuole da un ascoltatore.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        if (_host != null)
        {
            _host.Received -= OnArrived;
            _host.PeersChanged -= OnPeersChanged;
        }
        _host?.Dispose();
        _host = null;
        ReleaseLocks();
        base.OnDestroy();
    }

    // ----- arrivi -----

    /// <summary>
    /// E' arrivato qualcosa. Non finisce negli appunti da solo (vedi
    /// NetClipboardHost): si propone, con un'azione che lo mette li' in un tocco.
    /// </summary>
    private void OnArrived(ReceivedClip clip, HistoryItem item)
    {
        try
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(
                ArrivalChannelId, L.T("mobile.arrivalChannelName"), NotificationImportance.Default)
            {
                Description = L.T("mobile.arrivalChannelDescription"),
            });

            // Identificativo STABILE per voce di cronologia, non progressivo: se
            // lo stesso contenuto arrivasse due volte, la notifica si aggiorna
            // invece di impilarsi. Il mittente non dovrebbe ripetersi — e ora non
            // lo fa piu' — ma una notifica per arrivo era una scelta fragile, che
            // dipendeva dal comportamento di qualcun altro.
            var id = ArrivalNotificationId(item.Id);

            var open = PendingIntent.GetActivity(this, id,
                new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
                PendingIntentFlags.Immutable);

            var builder = new Notification.Builder(this, ArrivalChannelId)
                .SetContentTitle(L.T("mobile.arrivalTitle", clip.FromName))
                .SetContentText(clip.Payload.ShortPreview())
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetAutoCancel(true)
                .SetContentIntent(open);

            // L'azione rapida c'e' per cio' che sa andare negli appunti in un
            // colpo: il testo, e da questa versione anche le immagini (che ci
            // vanno come riferimento a un file, vedi AndroidClipboard.WriteImage).
            // I FILE no: quelli sono un'offerta da prelevare, ci vuole la rete e
            // ci vuole tempo, e una notifica non e' il posto per raccontarlo.
            // Li' si apre l'applicazione.
            if (clip.Payload.Kind is PayloadKind.Text or PayloadKind.Image)
            {
                var paste = PendingIntent.GetService(this, id,
                    new Intent(this, typeof(NetClipboardService))
                        .SetAction(ActionPaste)
                        .PutExtra(ExtraItemId, item.Id),
                    PendingIntentFlags.Immutable);
                builder.AddAction(new Notification.Action.Builder(null, L.T("mobile.putInClipboard"), paste).Build());
            }

            manager.Notify(id, builder.Build());
        }
        catch (Exception ex)
        {
            Log.Write($"[Android] arrivo non notificato: {ex.Message}");
        }
    }

    /// <summary>
    /// Numero di notifica ricavato dall'identificativo della voce: stesso
    /// contenuto, stessa notifica.
    /// </summary>
    private static int ArrivalNotificationId(string itemId)
    {
        var h = 5000;
        foreach (var c in itemId) h = unchecked(h * 31 + c);
        return h == int.MinValue ? 5000 : Math.Abs(h);
    }

    private void PasteFromNotification(Intent intent)
    {
        var itemId = intent.GetStringExtra(ExtraItemId);
        var host = NetClipboardHost.Current;
        if (itemId == null || host == null) return;

        var done = host.PutInClipboard(itemId);
        Log.Write($"[Android] messo negli appunti da notifica: {(done ? "sì" : "contenuto non più disponibile")}");
    }

    /// <summary>Avvia il servizio, se non gira gia'. Da chiamare all'apertura dell'applicazione.</summary>
    public static void Ensure(Context context)
    {
        var intent = new Intent(context, typeof(NetClipboardService));
        context.StartForegroundService(intent);
    }

    // ----- notifica -----

    /// <summary>
    /// Quanti dispositivi dice la notifica adesso. Le presenze cambiano di
    /// continuo (un giro di ping ogni tre secondi): si riscrive solo quando il
    /// numero cambia davvero, non a ogni battito.
    /// </summary>
    private int _shownPeers = -1;

    /// <summary>
    /// La notifica del servizio, che Android <b>impone</b>: e' la contropartita
    /// per restare in ascolto ad applicazione chiusa, e non si puo' nascondere.
    ///
    /// Se deve stare li', che dica qualcosa di utile: quanti dispositivi si
    /// vedono. Cosi' alla domanda "ma sta funzionando?" la risposta e' gia' nella
    /// barra, senza aprire niente. (Da Android 13 chi non la vuole puo' comunque
    /// scartarla scorrendola via: il servizio resta vivo.)
    /// </summary>
    private Notification BuildNotification(int trustedPeers)
    {
        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable);

        var stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(NetClipboardService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable);

        var text = trustedPeers switch
        {
            <= 0 => L.T("mobile.notifAlone"),
            1 => L.T("mobile.notifOnePeer"),
            _ => L.T("mobile.notifPeers", trustedPeers),
        };

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle(L.T("app.name"))
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetOngoing(true)
            .SetContentIntent(open)
            .AddAction(new Notification.Action.Builder(null, L.T("mobile.notifStop"), stop).Build())
            .Build();
    }

    private void OnPeersChanged()
    {
        var host = _host;
        if (host == null) return;

        var peers = host.Transport.TrustedPeers.Count;
        if (peers == _shownPeers) return;
        _shownPeers = peers;

        try
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.Notify(NotificationId, BuildNotification(peers));
        }
        catch (Exception ex)
        {
            Log.Write($"[Android] notifica di stato non aggiornata: {ex.Message}");
        }
    }

    private void ShowNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        // Importanza bassa: deve stare nella barra, non suonare.
        var channel = new NotificationChannel(ChannelId, L.T("mobile.channelName"), NotificationImportance.Low)
        {
            Description = L.T("mobile.channelDescription"),
        };
        manager.CreateNotificationChannel(channel);

        var notification = BuildNotification(0);

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
        else
            StartForeground(NotificationId, notification);
    }

    // ----- lucchetti del Wi-Fi -----

    /// <summary>
    /// Il lucchetto del multicast: senza, il sistema scarta i pacchetti di
    /// broadcast prima che arrivino all'applicazione. Ci si annuncerebbe agli
    /// altri senza sentire mai nessuno, e la scoperta funzionerebbe in un verso
    /// solo — con il risultato che il telefono vede i PC solo dopo che un PC ha
    /// provato a contattarlo.
    ///
    /// <para>Un <c>WifiLock</c> in piu' non c'e', ed e' una scelta: da Android 10
    /// <c>WIFI_MODE_FULL_HIGH_PERF</c> e' deprecato e il sistema lo tratta come
    /// un suggerimento. Il risparmio energetico della radio aggiunge frazioni di
    /// secondo, non perdite, e i nostri tempi di attesa sono di secondi. Se una
    /// prova sul campo dicesse il contrario, e' qui che si torna.</para>
    /// </summary>
    private void AcquireLocks()
    {
        try
        {
            var wifi = (WifiManager)ApplicationContext!.GetSystemService(WifiService)!;
            _multicast = wifi.CreateMulticastLock("netclipboard");
            _multicast!.SetReferenceCounted(false);
            _multicast.Acquire();
        }
        catch (Exception ex)
        {
            // Su una rete mobile il lucchetto del Wi-Fi non ha senso e puo' non
            // esserci: non e' un motivo per non partire.
            Log.Write($"[Android] lucchetto multicast non acquisito: {ex.Message}");
        }
    }

    private void ReleaseLocks()
    {
        try { if (_multicast?.IsHeld == true) _multicast.Release(); } catch { }
        _multicast = null;
    }

    /// <summary>
    /// Come si chiama questo telefono per gli altri. <c>Environment.MachineName</c>
    /// su Android risponde "localhost" a tutti, che in un elenco di dispositivi
    /// non distingue niente.
    /// </summary>
    private static string DeviceName()
    {
        var maker = Build.Manufacturer ?? "";
        var model = Build.Model ?? "Android";
        if (model.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) return model;
        return string.IsNullOrWhiteSpace(maker) ? model : $"{maker} {model}";
    }
}
