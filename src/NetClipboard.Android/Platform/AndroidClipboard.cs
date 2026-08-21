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
    /// </summary>
    /// <returns>
    /// Falso se il contenuto non e' testo. Le immagini hanno una strada loro
    /// (<see cref="WriteImage"/>), perche' negli appunti di Android non ci va
    /// l'immagine ma il RIFERIMENTO a un file. Si risponde invece di tacere,
    /// cosi' chi ha toccato la voce lo viene a sapere.
    /// </returns>
    public static bool Write(ClipboardPayload payload)
    {
        if (payload.Kind != PayloadKind.Text || payload.Text == null) return false;

        var clip = payload.Html != null
            ? ClipData.NewHtmlText(L.T("app.name"), payload.Text, payload.Html)
            : ClipData.NewPlainText(L.T("app.name"), payload.Text);
        return Put(clip);
    }

    /// <summary>
    /// Mette negli appunti un'immagine, cioe' il <b>riferimento</b> a un file che
    /// il nostro provider e' disposto a prestare (vedi <see cref="IncomingStore"/>).
    ///
    /// Non esiste un modo di mettere dei pixel negli appunti di Android: si mette
    /// un <c>content://</c>, e chi incolla lo apre. Il permesso di lettura lo
    /// concede il sistema a chi legge la clipboard, e scade da solo — per questo
    /// il file deve stare dietro al provider e non in una cartella qualunque.
    /// </summary>
    public static bool WriteImage(Android.Net.Uri uri)
    {
        var resolver = Application.Context.ContentResolver;
        if (resolver == null) return false;

        return Put(ClipData.NewUri(resolver, L.T("app.name"), uri));
    }

    /// <summary>
    /// Si passa dal thread principale perche' la clipboard e' un servizio di
    /// sistema e non tutte le versioni di Android accettano di riceverlo da un
    /// thread qualunque; qui arriviamo da un thread della rete.
    /// </summary>
    private static bool Put(ClipData? clip)
    {
        // Android puo' rispondere null: qui si risponde falso, invece di far
        // credere che il contenuto sia negli appunti.
        if (clip == null) return false;

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
        return true;
    }
}
