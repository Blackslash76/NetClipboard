using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using NetClipboard.Core;
using NetClipboard.Droid.Platform;

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

    private const int NotificationId = 4501;
    private const string ChannelId = "netclipboard.status";

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

        if (_host == null)
        {
            ShowNotification();
            AcquireLocks();
            _host = new NetClipboardHost(
                FilesDir?.AbsolutePath ?? CacheDir!.AbsolutePath,
                DeviceName());
            _host.Start();
        }

        // Sticky: se il sistema ci ferma per far posto a qualcos'altro, ci
        // rifa' partire da solo. E' cio' che si vuole da un ascoltatore.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        _host?.Dispose();
        _host = null;
        ReleaseLocks();
        base.OnDestroy();
    }

    /// <summary>Avvia il servizio, se non gira gia'. Da chiamare all'apertura dell'applicazione.</summary>
    public static void Ensure(Context context)
    {
        var intent = new Intent(context, typeof(NetClipboardService));
        context.StartForegroundService(intent);
    }

    // ----- notifica -----

    private void ShowNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        // Importanza bassa: deve stare nella barra, non suonare.
        var channel = new NotificationChannel(ChannelId, L.T("mobile.channelName"), NotificationImportance.Low)
        {
            Description = L.T("mobile.channelDescription"),
        };
        manager.CreateNotificationChannel(channel);

        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable);

        var stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(NetClipboardService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable);

        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle(L.T("app.name"))
            .SetContentText(L.T("mobile.notifRunning"))
            .SetSmallIcon(Android.Resource.Drawable.StatNotifySync)
            .SetOngoing(true)
            .SetContentIntent(open)
            .AddAction(new Notification.Action.Builder(null, L.T("mobile.notifStop"), stop).Build())
            .Build();

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
