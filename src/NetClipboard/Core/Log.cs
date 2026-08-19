using System.IO;

namespace NetClipboard.Core;

/// <summary>Log diagnostico minimale su file: %AppData%\NetClipboard\log.txt.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private const long MaxBytes = 1024 * 1024; // 1 MB, poi si azzera

    private static string? _override;

    public static string FilePath => _override ?? Path.Combine(AppConfig.AppDataDir, "log.txt");

    /// <summary>
    /// Manda il log altrove. Serve al banco di prova end-to-end, che tiene piu'
    /// istanze nello stesso processo e non deve scrivere nel log dell'applicazione
    /// di chi sta lavorando.
    /// </summary>
    public static void Redirect(string path) => _override = path;

    public static void Start(string header)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Delete(FilePath);
            }
        }
        catch { }
        Write("========================================");
        Write(header);
    }

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}\r\n");
        }
        catch { }
    }
}
