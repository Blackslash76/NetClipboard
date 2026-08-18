using System.IO;

namespace NetClipboard.Core;

/// <summary>
/// Presenza di NetClipboard nel menu "Invia a" di Windows.
///
/// Non serve una shell extension: la cartella <c>shell:sendto</c> è il modo
/// previsto dal sistema. Un collegamento lì dentro compare nel menu contestuale
/// di qualunque selezione di file, ed Explorer passa i percorsi selezionati come
/// argomenti al programma. Niente registrazioni COM, niente pacchetti, e si
/// disinstalla cancellando un file.
///
/// Il collegamento si crea con lo scripting host di Windows chiamato per nome:
/// creare un .lnk richiederebbe altrimenti l'interfaccia IShellLink e un
/// pacchetto di interoperabilità in più nell'eseguibile.
/// </summary>
public static class SendToShortcut
{
    /// <summary>Argomento con cui Explorer avvia l'app dal menu "Invia a".</summary>
    public const string Argument = "--send-to";

    private const string LinkName = "NetClipboard.lnk";

    private static string Folder =>
        Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    private static string LinkPath => Path.Combine(Folder, LinkName);

    public static bool Installed
    {
        get
        {
            try { return File.Exists(LinkPath); }
            catch { return false; }
        }
    }

    /// <summary>Crea o rimuove la voce. Restituisce lo stato effettivo raggiunto.</summary>
    public static bool Apply(bool wanted)
    {
        try
        {
            if (!wanted)
            {
                if (File.Exists(LinkPath)) File.Delete(LinkPath);
                Log.Write("[SendTo] voce rimossa dal menu \"Invia a\".");
                return false;
            }

            if (!Create()) return false;
            Log.Write("[SendTo] voce creata nel menu \"Invia a\".");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[SendTo] impossibile aggiornare la voce: {ex.Message}");
            return Installed;
        }
    }

    private static bool Create()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            Log.Write("[SendTo] scripting host non disponibile: voce non creata.");
            return false;
        }

        object? shell = null;
        object? link = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            link = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
            if (link == null) return false;

            var t = link.GetType();
            void Set(string name, string value) => t.InvokeMember(name,
                System.Reflection.BindingFlags.SetProperty, null, link, new object[] { value });

            Set("TargetPath", exe);
            Set("Arguments", Argument);
            Set("IconLocation", exe + ",0");
            Set("Description", "Invia i file selezionati con NetClipboard");
            Set("WorkingDirectory", Path.GetDirectoryName(exe) ?? "");

            t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
            return File.Exists(LinkPath);
        }
        finally
        {
            if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            if (shell != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
