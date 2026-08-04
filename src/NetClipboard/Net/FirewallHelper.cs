using System.Diagnostics;
using System.Security.Principal;
using NetClipboard.Core;

namespace NetClipboard.Net;

/// <summary>
/// Aggiunge/rimuove la regola del Windows Firewall per la nostra app.
/// Non "aggira" nulla: crea una normale eccezione per l'eseguibile, il che
/// richiede privilegi di amministratore una sola volta. Se non siamo elevati,
/// rilanciamo noi stessi con l'argomento --install-firewall tramite UAC.
/// </summary>
public static class FirewallHelper
{
    private const string RuleName = "NetClipboard";

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Chiede l'elevazione UAC e installa le regole. Ritorna false se annullato.</summary>
    public static bool RequestInstallElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--install-firewall",
                UseShellExecute = true,
                Verb = "runas", // trigger UAC
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(15000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false; // utente ha annullato l'UAC
        }
    }

    /// <summary>Esegue effettivamente i comandi netsh (richiede di essere gia' elevati).</summary>
    public static int InstallRulesNow()
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return 1;

        var port = AppConfig.Load().Port;
        Log.Write($"[Firewall] installo regole · elevato={IsElevated()} · exe={exe} · porta={port}");

        // Rimuove eventuali regole precedenti (ignora errori), poi le ricrea.
        RunNetsh($"advfirewall firewall delete rule name=\"{RuleName}\"");

        // 1) Regole per programma (qualunque porta usi la nostra app).
        var rc1 = RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");
        var rc2 = RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=out action=allow program=\"{exe}\" enable=yes profile=any");

        // 2) Regole per porta (UDP scoperta + TCP trasferimento), belt-and-suspenders.
        var rc3 = RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport={port} enable=yes profile=any");
        var rc4 = RunNetsh($"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port} enable=yes profile=any");

        var ok = rc1 == 0 && rc2 == 0 && rc3 == 0 && rc4 == 0;
        Log.Write($"[Firewall] esiti netsh: prog-in={rc1} prog-out={rc2} udp-in={rc3} tcp-in={rc4} -> {(ok ? "OK" : "ERRORE")}");
        return ok ? 0 : 1;
    }

    private static int RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return 1;
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch
        {
            return 1;
        }
    }
}
