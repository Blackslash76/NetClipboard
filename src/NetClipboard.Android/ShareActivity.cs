using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using NetClipboard.Core;
using NetClipboard.Droid.Platform;
using NetClipboard.Droid.Services;
using Uri = Android.Net.Uri;

namespace NetClipboard.Droid;

/// <summary>
/// La destinazione di "Condividi". E' il modo naturale di mandare qualcosa da un
/// telefono, ed e' anche l'unico praticabile: Android vieta di leggere gli
/// appunti se non si e' in primo piano, e immagini e file negli appunti non ci
/// finiscono quasi mai. Condividendo, invece, e' il sistema stesso a passarci il
/// contenuto — con il consenso esplicito di chi tocca.
///
/// Non ha interfaccia: prende, copia, manda, dice com'e' andata e sparisce. La
/// conferma e' il gesto stesso di scegliere NetClipboard nel foglio di
/// condivisione, come sul PC la voce "Invia ora ai miei dispositivi".
/// </summary>
[Activity(
    Label = "@string/app_name",
    Exported = true,
    Theme = "@style/NetClipboardTheme.Invisible",
    NoHistory = true,
    ExcludeFromRecents = true,
    LaunchMode = LaunchMode.SingleTop)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public class ShareActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _ = HandleAsync();
    }

    private async Task HandleAsync()
    {
        try
        {
            NetClipboardService.Ensure(this);

            var host = await WaitForHostAsync();
            if (host == null) { Say(L.T("mobile.shareNoService")); return; }

            // La copia dei contenuti DEVE finire prima di Finish(): il permesso
            // di leggere gli URI condivisi muore con questa activity.
            var payload = await Task.Run(() => BuildPayload(host));
            if (payload == null) { Say(L.T("mobile.shareNothing")); return; }

            var sent = await host.ShareAsync(payload);
            Say(sent == 0 ? L.T("msg.noTrustedOnline") : L.T("msg.sentTo", sent));
        }
        catch (Exception ex)
        {
            Log.Write($"[Share] non riuscita: {ex}");
            Say(L.T("mobile.shareFailed"));
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>
    /// Il servizio potrebbe essere appena partito. Si aspetta qualche istante,
    /// invece di dire "non c'e'" a chi ha appena condiviso qualcosa.
    /// </summary>
    private static async Task<NetClipboardHost?> WaitForHostAsync()
    {
        for (var i = 0; i < 40 && NetClipboardHost.Current == null; i++)
            await Task.Delay(100);
        return NetClipboardHost.Current;
    }

    private ClipboardPayload? BuildPayload(NetClipboardHost host)
    {
        var intent = Intent;
        if (intent == null) return null;

        // Testo condiviso senza allegati: viaggia come testo, non come file.
        if (intent.Action == Intent.ActionSend && !intent.HasExtra(Intent.ExtraStream))
        {
            var text = intent.GetStringExtra(Intent.ExtraText);
            return string.IsNullOrEmpty(text) ? null : ClipboardPayload.FromText(text);
        }

        var uris = ReadUris(intent);
        if (uris.Count == 0) return null;

        // Foto e file prendono la stessa strada: un'offerta.
        //
        // Anche le foto, di proposito. Sul filo PayloadKind.Image e' un PNG e non
        // porta il tipo del contenuto: un JPEG da 3 MB ricodificato in PNG ne
        // diventa venti o trenta, sfonda il tetto dei trasferimenti e spreca rete
        // per niente. Come offerta, invece, arriva il file originale — e non
        // parte niente finche' dall'altra parte non lo si vuole davvero.
        var offer = OutgoingStore.Capture(this, uris, host.Identity.DeviceId, host.Config.DisplayName);
        return offer == null ? null : ClipboardPayload.FromOffer(offer);
    }

    /// <summary>
    /// Gli URI allegati alla condivisione.
    ///
    /// Da Android 13 la lettura senza tipo e' deprecata e si passa la classe
    /// attesa: cosi' e' il sistema a rifiutare un extra di tipo diverso, invece
    /// di scoprirlo noi con un cast. Sotto quella versione esiste solo la forma
    /// vecchia, quindi restano tutt'e due.
    /// </summary>
    private static List<Uri> ReadUris(Intent intent)
    {
        var uris = new List<Uri>();
        var uriClass = Java.Lang.Class.FromType(typeof(Uri));

        if (intent.Action == Intent.ActionSendMultiple)
        {
            var list = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableArrayListExtra(Intent.ExtraStream, uriClass)
                : intent.GetParcelableArrayListExtra(Intent.ExtraStream);
            if (list != null)
                foreach (var item in list)
                    if (item is Uri uri)
                        uris.Add(uri);
            return uris;
        }

        var single = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? intent.GetParcelableExtra(Intent.ExtraStream, uriClass)
            : intent.GetParcelableExtra(Intent.ExtraStream);
        if (single is Uri only) uris.Add(only);
        return uris;
    }

    private void Say(string message) =>
        RunOnUiThread(() => Toast.MakeText(this, message, ToastLength.Long)?.Show());
}
