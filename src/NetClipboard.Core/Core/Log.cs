using System.IO;

namespace NetClipboard.Core;

/// <summary>Log diagnostico minimale su file: %AppData%\NetClipboard\log.txt.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    /// <summary>Oltre questa dimensione il log gira. Se ne tengono due: al massimo il doppio.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Ogni quanti byte scritti ci si ferma a guardare la dimensione del file.
    ///
    /// Non a ogni riga: una <c>FileInfo</c> per riga sarebbe un accesso al disco
    /// per ogni riga di log. Non solo all'avvio: <b>era cosi', ed era il difetto</b>
    /// — il controllo stava in <see cref="Start"/> e basta, quindi un processo che
    /// resta su per settimane non lo faceva mai. Sul telefono il servizio in primo
    /// piano vive esattamente cosi', e il log era arrivato a 851 KB in mezza
    /// giornata senza che nessuno lo guardasse.
    /// </summary>
    private const long CheckEvery = 64 * 1024;

    private static long _sinceCheck;
    private static string? _override;

    public static string FilePath => _override ?? Path.Combine(AppConfig.AppDataDir, "log.txt");

    /// <summary>Il giro precedente. Si tiene: il difetto interessante di solito e' appena prima della rotazione.</summary>
    private static string PreviousPath
    {
        get
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".prev" + Path.GetExtension(path));
        }
    }

    /// <summary>
    /// Manda il log altrove. Serve al banco di prova end-to-end, che tiene piu'
    /// istanze nello stesso processo e non deve scrivere nel log dell'applicazione
    /// di chi sta lavorando.
    /// </summary>
    public static void Redirect(string path)
    {
        lock (Gate)
        {
            _override = path;
            _sinceCheck = 0;
        }
    }

    public static void Start(string header)
    {
        lock (Gate)
        {
            _sinceCheck = 0;
            RotateIfBigUnlocked();
        }
        Write("========================================");
        Write(header);
    }

    public static void Write(string msg)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}\r\n";
            lock (Gate)
            {
                File.AppendAllText(FilePath, line);

                _sinceCheck += line.Length;
                if (_sinceCheck < CheckEvery) return;
                _sinceCheck = 0;
                RotateIfBigUnlocked();
            }
        }
        catch { }
    }

    /// <summary>
    /// Se il file ha passato il tetto, diventa il "precedente" e se ne comincia
    /// uno nuovo. Il vecchio precedente si perde: due generazioni bastano a capire
    /// cos'e' successo, e il patto e' che questa cartella non cresca.
    /// </summary>
    private static void RotateIfBigUnlocked()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path) || new FileInfo(path).Length <= MaxBytes) return;

            var previous = PreviousPath;
            try { if (File.Exists(previous)) File.Delete(previous); } catch { }

            try { File.Move(path, previous); }
            catch { File.Delete(path); } // non si e' potuto spostare: almeno non cresce
        }
        catch { }
    }
}
