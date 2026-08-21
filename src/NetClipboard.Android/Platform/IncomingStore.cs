using Android.Content;
using Android.Provider;
using Android.Webkit;
using NetClipboard.Core;
using AndroidUri = Android.Net.Uri;
using Environment = Android.OS.Environment;

namespace NetClipboard.Droid.Platform;

/// <summary>
/// Dove finisce cio' che arriva: il gemello in entrata di <see cref="OutgoingStore"/>.
///
/// Il prelievo dei file scrive in una cartella privata dell'applicazione — e'
/// l'unico posto dove <c>ClipboardTransport.FetchAsync</c> puo' lavorare con
/// percorsi normali — ma li' dentro non li vedrebbe nessuno: la memoria privata
/// non compare in "File", non compare in Download, e da Android 10 nemmeno un
/// gestore di file la puo' aprire. Un file ricevuto e invisibile e' un file non
/// ricevuto.
///
/// Percio' dopo il prelievo si <b>pubblicano</b> in <c>MediaStore.Downloads</c>,
/// che e' il Download del telefono: compaiono dove l'utente li cerca, e da
/// Android 10 farlo non richiede alcun permesso — e' il sistema a scrivere per
/// conto nostro, dentro una cartella che e' nostra.
/// </summary>
public static class IncomingStore
{
    /// <summary>Sottocartella di Download che raccoglie tutto cio' che arriva da qui.</summary>
    private const string Folder = "NetClipboard";

    /// <summary>Cartella privata da cui il provider presta i file agli appunti.</summary>
    private const string ClipDir = "clip";

    /// <summary>Deve coincidere con l'authority dichiarata nel manifest.</summary>
    private static string Authority => Android.App.Application.Context.PackageName + ".files";

    // ----- pubblicazione in Download -----

    /// <summary>
    /// Copia in Download tutto cio' che sta sotto le radici indicate, ricostruendo
    /// le sottocartelle, e restituisce quanti file sono arrivati a destinazione.
    /// </summary>
    /// <param name="destDir">La cartella in cui il prelievo ha scritto: serve a calcolare i percorsi relativi.</param>
    public static int PublishToDownloads(Context context, string destDir, IReadOnlyList<string> roots)
    {
        var resolver = context.ContentResolver;
        if (resolver == null) return 0;

        var saved = 0;
        foreach (var file in Walk(roots))
        {
            try
            {
                if (Publish(resolver, destDir, file)) saved++;
            }
            catch (Exception ex)
            {
                // Un file che non si riesce a pubblicare non deve fermare gli altri:
                // meglio nove file su dieci che nessuno.
                Log.Write($"[Android] '{Path.GetFileName(file)}' non pubblicato: {ex.Message}");
            }
        }
        return saved;
    }

    private static bool Publish(ContentResolver resolver, string destDir, string file)
    {
        var relative = Path.GetRelativePath(destDir, file);
        var subdir = Path.GetDirectoryName(relative);
        var target = Environment.DirectoryDownloads + "/" + Folder;
        if (!string.IsNullOrEmpty(subdir))
            target += "/" + subdir.Replace('\\', '/');

        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, Path.GetFileName(file));
        values.Put(MediaStore.IMediaColumns.MimeType, MimeFor(file));
        values.Put(MediaStore.IMediaColumns.RelativePath, target);

        // "In sospeso" finche' i byte non ci sono tutti: senza, un'altra
        // applicazione potrebbe aprire il file a meta' scrittura e trovarlo
        // troncato — e un'immagine troncata sembra un file corrotto, non un
        // trasferimento ancora in corso.
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values);
        if (uri == null) return false;

        try
        {
            using (var output = resolver.OpenOutputStream(uri))
            {
                if (output == null) throw new IOException("flusso di scrittura non disponibile");
                using var input = File.OpenRead(file);
                input.CopyTo(output);
            }

            var done = new ContentValues();
            done.Put(MediaStore.IMediaColumns.IsPending, 0);
            resolver.Update(uri, done, null, null);
            return true;
        }
        catch
        {
            // Riga a meta': si toglie, invece di lasciare in Download un file
            // vuoto che sembra arrivato.
            try { resolver.Delete(uri, null, null); } catch (Exception ex) { Log.Write($"[Android] riga in sospeso non rimossa: {ex.Message}"); }
            throw;
        }
    }

    private static IEnumerable<string> Walk(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (File.Exists(root))
            {
                yield return root;
                continue;
            }
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return "application/octet-stream";
        var mime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext);
        return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
    }

    // ----- prestito agli appunti -----

    /// <summary>
    /// Scrive dei byte in un file prestabile e ne restituisce l'URI da mettere
    /// negli appunti.
    ///
    /// Il nome viene dall'identificativo della voce, quindi mettere due volte
    /// negli appunti la stessa immagine riscrive lo stesso file invece di
    /// riempire il disco di copie.
    /// </summary>
    public static AndroidUri? Stage(Context context, byte[] bytes, string itemId, string extension)
    {
        try
        {
            var dir = Path.Combine(context.FilesDir!.AbsolutePath, ClipDir);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Safe(itemId) + extension);
            File.WriteAllBytes(path, bytes);
            return AndroidX.Core.Content.FileProvider.GetUriForFile(context, Authority, new Java.IO.File(path));
        }
        catch (Exception ex)
        {
            Log.Write($"[Android] contenuto non preparato per gli appunti: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Butta via i file prestati agli appunti piu' vecchi di un giorno.
    ///
    /// Non si possono cancellare subito dopo averli messi in clipboard: chi
    /// incolla li legge quando gli pare, anche molto dopo. Un giorno e' molto piu'
    /// di quanto duri un "copia e incolla" e molto meno di quanto serva perche' la
    /// cartella diventi un problema.
    /// </summary>
    public static void PruneStaged(Context context)
    {
        try
        {
            var dir = Path.Combine(context.FilesDir!.AbsolutePath, ClipDir);
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                try { File.Delete(file); } catch (IOException) { } // in uso: al prossimo giro
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Android] cartella degli appunti non ripulita: {ex.Message}");
        }
    }

    private static string Safe(string id) =>
        new(id.Where(c => char.IsLetterOrDigit(c)).Take(40).ToArray());
}
