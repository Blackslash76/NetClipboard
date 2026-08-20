using Android.Content;
using Android.OS;
using NetClipboard.Core;
using Application = Android.App.Application;

namespace NetClipboard.Droid.Platform;

/// <summary>
/// La clipboard di Android.
///
/// <para><b>Il vincolo che decide come e' fatta questa applicazione.</b> Da
/// Android 10 la clipboard si puo' LEGGERE solo mentre si e' in primo piano (o
/// essendo la tastiera predefinita, o un servizio di accessibilita': due strade
/// che qui non si prendono). Quindi il rispecchiamento automatico funziona in un
/// verso solo: dal PC al telefono si', dal telefono al PC no. Dal telefono si
/// manda con un gesto — un tocco, la condivisione da un'altra applicazione — e
/// quel gesto ci mette in primo piano, che e' esattamente cio' che serve.
///
/// Non e' un limite da aggirare: e' il sistema che protegge cio' che l'utente
/// copia, incluse le password. La stessa cautela che su Windows si e' scritta a
/// mano guardando i marcatori dei gestori di password, qui la impone Android.</para>
/// </summary>
public static class AndroidClipboard
{
    private static ClipboardManager? Manager =>
        Application.Context.GetSystemService(Context.ClipboardService) as ClipboardManager;

    /// <summary>
    /// Il contenuto attuale, o null se non c'e' testo. Da chiamare solo mentre
    /// l'applicazione e' in primo piano: altrove Android restituisce null e basta.
    /// </summary>
    public static ClipboardPayload? Read()
    {
        var clip = Manager?.PrimaryClip;
        if (clip == null || clip.ItemCount == 0) return null;

        var item = clip.GetItemAt(0);
        if (item == null) return null;

        var text = item.CoerceToText(Application.Context)?.ToString();
        if (string.IsNullOrEmpty(text)) return null;

        // Se chi ha copiato ha portato con se' l'HTML, viaggia anche quello: e' la
        // stessa coda facoltativa che manda Windows, e dall'altra parte la
        // formattazione ricompare.
        var html = item.HtmlText;
        return string.IsNullOrEmpty(html)
            ? ClipboardPayload.FromText(text)
            : ClipboardPayload.FromRichText(text, html, null);
    }

    /// <summary>
    /// Mette in clipboard cio' che e' arrivato dalla rete.
    ///
    /// Scrivere e' permesso anche da un servizio in background — e' la LETTURA a
    /// essere vietata — quindi il contenuto arriva negli appunti anche ad
    /// applicazione chiusa, che poi e' il caso normale: si copia sul PC e si va a
    /// incollare sul telefono.
    ///
    /// Si passa dal thread principale perche' la clipboard e' un servizio di
    /// sistema e non tutte le versioni di Android accettano di riceverlo da un
    /// thread qualunque; qui arriviamo da un thread della rete.
    /// </summary>
    public static void Write(ClipboardPayload payload)
    {
        if (payload.Kind != PayloadKind.Text || payload.Text == null) return;

        var clip = payload.Html != null
            ? ClipData.NewHtmlText(L.T("app.name"), payload.Text, payload.Html)
            : ClipData.NewPlainText(L.T("app.name"), payload.Text);
        if (clip == null) return;

        new Handler(Looper.MainLooper!).Post(() =>
        {
            try
            {
                var manager = Manager;
                if (manager != null) manager.PrimaryClip = clip;
            }
            catch (Exception ex)
            {
                Log.Write($"[Android] appunti non aggiornati: {ex.Message}");
            }
        });
    }
}
