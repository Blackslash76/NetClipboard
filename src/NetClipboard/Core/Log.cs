using System.IO;

namespace NetClipboard.Core;

/// <summary>Log diagnostico minimale su file: %AppData%\NetClipboard\log.txt.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private const long MaxBytes = 1024 * 1024; // 1 MB, poi si azzera

    public static string FilePath => Path.Combine(AppConfig.AppDataDir, "log.txt");

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
