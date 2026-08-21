using Android.Content;
using Android.Database;
using Android.Provider;
using NetClipboard.Core;
using Uri = Android.Net.Uri;

namespace NetClipboard.Droid.Platform;

/// <summary>
/// Cio' che l'utente condivide verso NetClipboard, copiato nella memoria privata
/// dell'applicazione.
///
/// <para><b>Perche' si copia invece di puntare all'originale.</b> Il nostro
/// modello dei file e' a rendering ritardato: sul filo viaggia l'elenco, i byte
/// si chiedono dopo, quando chi riceve incolla. Ma cio' che la condivisione di
/// Android ci passa e' un <c>content://</c> con un permesso <b>temporaneo</b>,
/// legato all'activity che lo ha ricevuto: quando quella finisce, il diritto di
/// leggere svanisce. Il PC chiede i byte minuti dopo, e li' non ci sarebbe piu'
/// niente da leggere.
///
/// Quindi si copia subito, finche' il permesso c'e', e l'offerta si costruisce
/// sulle copie. Costa disco — ed e' il motivo per cui esiste
/// <see cref="Prune"/>.</para>
/// </summary>
public static class OutgoingStore
{
    /// <summary>Le condivisioni piu' vecchie di così si buttano.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    /// <summary>
    /// Tetto complessivo: oltre, si buttano le più vecchie finché si rientra.
    ///
    /// Era mezzo giga, che è un numero da PC: su un telefono sono copie di file
    /// che l'utente ha già, tenute per un giorno, e mezzo giga di roba nostra si
    /// nota. La regola inoltre la faceva scattare solo una nuova condivisione —
    /// ora la chiama anche <see cref="Housekeeping"/>, all'avvio e agli arrivi.
    /// </summary>
    private const long MaxTotalBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Copia i contenuti condivisi e ne fa un'offerta. Va chiamata mentre
    /// l'activity che ha ricevuto la condivisione e' ancora viva.
    /// </summary>
    public static FileOffer? Capture(Context context, IReadOnlyList<Uri> uris, string ownerDeviceId, string ownerName)
    {
        if (uris.Count == 0) return null;

        var root = Path.Combine(OutgoingRoot(context), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var copied = new List<string>();
        foreach (var uri in uris)
        {
            try
            {
                var dest = UniquePath(root, SafeName(DisplayName(context, uri)));
                using (var input = context.ContentResolver!.OpenInputStream(uri))
                {
                    if (input == null) continue;
                    using var output = File.Create(dest);
                    input.CopyTo(output);
                }
                StripLocation(dest);
                CarryOverDate(context, uri, dest);
                copied.Add(dest);
            }
            catch (Exception ex)
            {
                Log.Write($"[Share] non copiato {uri}: {ex.Message}");
            }
        }

        if (copied.Count == 0)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            return null;
        }

        Prune(context);

        var offer = FileOffer.FromPaths(copied, ownerDeviceId, ownerName);
        if (offer != null) offer.Thumbnail = MakeThumbnail(copied);
        return offer;
    }

    /// <summary>
    /// Una miniatura del primo contenuto che sia un'immagine, o null.
    ///
    /// Serve a chi riceve: nel rendering ritardato ha solo nomi e dimensioni
    /// finche' non scarica, e di una foto vedrebbe soltanto il nome. Cosi' invece
    /// vede cosa sta per prendersi.
    ///
    /// Si decodifica <b>gia' in scala ridotta</b> (<c>InSampleSize</c>): una foto
    /// da dodici megapixel decodificata per intero sono una cinquantina di
    /// megabyte in memoria, per farne un quadratino.
    /// </summary>
    private static byte[]? MakeThumbnail(IReadOnlyList<string> files)
    {
        const int target = 256;

        var image = files.FirstOrDefault(IsImage);
        if (image == null) return null;

        try
        {
            var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
            Android.Graphics.BitmapFactory.DecodeFile(image, bounds);
            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

            var sample = 1;
            while (bounds.OutWidth / (sample * 2) >= target && bounds.OutHeight / (sample * 2) >= target)
                sample *= 2;

            using var bitmap = Android.Graphics.BitmapFactory.DecodeFile(
                image, new Android.Graphics.BitmapFactory.Options { InSampleSize = sample });
            if (bitmap == null) return null;

            using var stream = new MemoryStream();
            // JPEG e non PNG: e' una fotografia, e in PNG peserebbe molte volte
            // tanto per la stessa resa.
            bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 70, stream);
            var bytes = stream.ToArray();

            return bytes.Length <= FileOffer.MaxThumbnailBytes ? bytes : null;
        }
        catch (Exception ex)
        {
            Log.Write($"[Share] miniatura non creata: {ex.Message}");
            return null;
        }
    }

    private static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".bmp" or ".heic";
    }

    private static string OutgoingRoot(Context context)
    {
        var dir = Path.Combine(context.FilesDir!.AbsolutePath, "outgoing");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ----- nome del file -----

    /// <summary>
    /// Il nome che il sistema associa al contenuto. Un <c>content://</c> non ha
    /// un nome nel percorso: lo si chiede al fornitore, e se non risponde si
    /// ripiega sull'ultimo segmento.
    /// </summary>
    /// <summary>
    /// Mette sulla copia la data di modifica dell'originale.
    ///
    /// Senza questo, la copia porta la data in cui l'abbiamo fatta — cioe' adesso
    /// — e siccome l'impronta di un'offerta guarda anche la data, <b>la stessa
    /// foto condivisa due volte risulterebbe due contenuti diversi</b>: due righe
    /// in cronologia invece del riuso della voce esistente. La condivisione da
    /// Android e' proprio il caso in cui si ricondivide la stessa cosa.
    ///
    /// Se il fornitore non espone la data si lascia stare: la voce restera' a
    /// zero, cioe' "non nota", e l'impronta tornera' a guardare nome e dimensione
    /// come faceva prima. Meglio nessuna data che una data inventata.
    /// </summary>
    private static void CarryOverDate(Context context, Uri uri, string dest)
    {
        try
        {
            using ICursor? cursor = context.ContentResolver!.Query(
                uri, new[] { MediaStore.IMediaColumns.DateModified }, null, null, null);
            if (cursor == null || !cursor.MoveToFirst()) return;

            var i = cursor.GetColumnIndex(MediaStore.IMediaColumns.DateModified);
            if (i < 0 || cursor.IsNull(i)) return;

            // MediaStore la da' in SECONDI dall'epoca, non in millisecondi.
            var seconds = cursor.GetLong(i);
            if (seconds <= 0) return;

            File.SetLastWriteTimeUtc(dest, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime);
        }
        catch (Exception ex)
        {
            Log.Write($"[Share] data dell'originale non riportata: {ex.Message}");
        }
    }

    private static string DisplayName(Context context, Uri uri)
    {
        try
        {
            using ICursor? cursor = context.ContentResolver!.Query(
                uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                var i = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (i >= 0)
                {
                    var name = cursor.GetString(i);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Share] nome non ottenuto: {ex.Message}");
        }
        return uri.LastPathSegment ?? "file";
    }

    /// <summary>
    /// Il nome arriva da fuori: qui diventa un nome di file e basta. Chi riceve
    /// fa i suoi controlli sul percorso (SafeTarget), ma un nome ostile non deve
    /// nemmeno partire da qui.
    /// </summary>
    private static string SafeName(string raw)
    {
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        return string.IsNullOrEmpty(name) ? "file" : name;
    }

    /// <summary>Due contenuti condivisi insieme possono chiamarsi uguale.</summary>
    private static string UniquePath(string dir, string name)
    {
        var candidate = Path.Combine(dir, name);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    // ----- posizione nelle foto -----

    /// <summary>
    /// Toglie le coordinate GPS da una fotografia, e <b>solo</b> quelle.
    ///
    /// Cancellare tutti i metadati sembrerebbe più prudente ed e' peggio:
    /// nell'EXIF c'e' anche l'orientamento, e senza quello le foto si vedono
    /// ruotate. La posizione e' la parte davvero sensibile — una foto mandata al
    /// proprio PC non ha motivo di portarsi dietro dove e' stata scattata — e si
    /// toglie chirurgicamente. Data, orientamento e modello restano.
    ///
    /// Si lavora sulla copia, mai sull'originale nella galleria.
    /// </summary>
    private static void StripLocation(string path)
    {
        if (!IsJpeg(path)) return;
        try
        {
            var exif = new Android.Media.ExifInterface(path);
            foreach (var tag in new[]
                     {
                         Android.Media.ExifInterface.TagGpsLatitude,
                         Android.Media.ExifInterface.TagGpsLatitudeRef,
                         Android.Media.ExifInterface.TagGpsLongitude,
                         Android.Media.ExifInterface.TagGpsLongitudeRef,
                         Android.Media.ExifInterface.TagGpsAltitude,
                         Android.Media.ExifInterface.TagGpsAltitudeRef,
                         Android.Media.ExifInterface.TagGpsTimestamp,
                         Android.Media.ExifInterface.TagGpsDatestamp,
                         Android.Media.ExifInterface.TagGpsProcessingMethod,
                     })
                exif.SetAttribute(tag, null);
            exif.SaveAttributes();
        }
        catch (Exception ex)
        {
            // Non si manda una foto che non si e' riusciti a ripulire.
            Log.Write($"[Share] posizione non rimossa da {Path.GetFileName(path)}: {ex.Message}");
            try { File.Delete(path); } catch { }
        }
    }

    private static bool IsJpeg(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg";
    }

    // ----- pulizia -----

    /// <summary>
    /// Su Windows un'offerta punta ai file dell'utente e non costa niente; qui
    /// sono copie vere, e senza pulizia la memoria del telefono si riempirebbe di
    /// roba condivisa mesi prima.
    /// </summary>
    public static void Prune(Context context)
    {
        try
        {
            var root = OutgoingRoot(context);
            var dirs = new DirectoryInfo(root).GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ToList();

            var now = DateTime.UtcNow;
            long total = 0;
            foreach (var dir in dirs)
            {
                var size = SizeOf(dir);
                total += size;
                if (now - dir.LastWriteTimeUtc > KeepFor || total > MaxTotalBytes)
                {
                    try { dir.Delete(recursive: true); total -= size; } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[Share] pulizia non riuscita: {ex.Message}");
        }
    }

    private static long SizeOf(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }
}
