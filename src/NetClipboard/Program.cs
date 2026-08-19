using System.IO;
using System.Threading;
using NetClipboard.Core;
using NetClipboard.Net;
using NetClipboard.Ui;
using NetClipboard.Update;

namespace NetClipboard;

internal static class Program
{
    private const string MutexName = "NetClipboard.SingleInstance.6f0c2b1e";

    [STAThread]
    private static int Main(string[] args)
    {
        // Modalita' speciale: installa le regole firewall (rilanciata elevata via UAC).
        if (args.Length > 0 && string.Equals(args[0], "--install-firewall", StringComparison.OrdinalIgnoreCase))
            return FirewallHelper.InstallRulesNow();

        // Modalita' speciale: prende il posto dell'eseguibile installato (rilanciata
        // elevata via UAC quando l'app sta in Program Files e non puo' sostituirsi da
        // sola). Qui gira il binario NUOVO, gia' verificato contro lo SHA-256 firmato.
        //   --apply-update <exe da sostituire> <pid da attendere>
        if (args.Length > 2 && string.Equals(args[0], Updater.ApplyArgument, StringComparison.OrdinalIgnoreCase))
            return Updater.RunApplyHelper(args[1], int.TryParse(args[2], out var pid) ? pid : 0);

        // Strumenti sviluppatore per il rilascio (nessuna UI):
        //   --gen-release-key <privateOut> <publicOut>
        //   --sign-release <exe> <version> <privateKeyFile> <exeUrl> <manifestOut> [note]
        if (args.Length > 0 && args[0] == "--gen-release-key")
        {
            try
            {
                var (priv, pub) = Updater.GenerateKeypair();
                File.WriteAllText(args.Length > 1 ? args[1] : "release-signing.key", priv);
                File.WriteAllText(args.Length > 2 ? args[2] : "release-public.txt", pub);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText("keygen-error.txt", ex.ToString());
                return 1;
            }
        }
        if (args.Length > 0 && args[0] == "--sign-release")
        {
            try
            {
                var note = args.Length > 6 ? args[6] : "";
                var json = Updater.BuildManifestJson(args[1], args[2], File.ReadAllText(args[3]).Trim(), args[4], note);
                File.WriteAllText(args[5], json);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText("sign-error.txt", ex.ToString());
                return 1;
            }
        }

        // Modalita' senza interfaccia: registra l'avvio automatico per QUESTO utente
        // e esce. La usa l'installer: gira elevato, e da li' non saprebbe scrivere nel
        // profilo giusto — con "runasoriginaluser" invece questo processo e' proprio
        // dell'utente vero. Cosi' l'avvio automatico resta un fatto solo suo, con un
        // unico padrone (HKCU\...\Run) invece di un collegamento che dice un'altra cosa.
        if (args.Length > 0 && string.Equals(args[0], "--set-autostart", StringComparison.OrdinalIgnoreCase))
        {
            var cfg = AppConfig.Load();
            cfg.StartWithWindows = true;
            cfg.Save();
            SettingsForm.ApplyAutoStart(true);
            return 0;
        }

        // Avviato dal menu "Invia a" di Windows: Explorer passa i percorsi
        // selezionati come argomenti. Questo processo non ha rete ne' dispositivi,
        // quindi consegna i percorsi all'istanza gia' in esecuzione e termina.
        if (args.Length > 0 && string.Equals(args[0], SendToShortcut.Argument, StringComparison.OrdinalIgnoreCase))
        {
            var paths = args.Skip(1).Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
            if (paths.Count == 0) return 0;

            if (!InstanceBridge.TrySend(paths))
            {
                NetClipboard.Core.L.Init();
                MessageBox.Show(NetClipboard.Core.L.T("sendto.notRunning"),
                                NetClipboard.Core.L.T("app.name"),
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        // Una sola istanza per utente.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
            return 0;

        ApplicationConfiguration.Initialize();

        // Modalita' chiara/scura come la vuole Windows. Questa chiamata riguarda i
        // controlli di sistema (liste, caselle, barre di scorrimento); i disegni
        // nostri leggono la palette da Theme. E' ancora un'API sperimentale, per
        // questo il warning va zittito qui e solo qui.
#pragma warning disable WFO5001
        try { Application.SetColorMode(SystemColorMode.System); } catch { }
#pragma warning restore WFO5001
        Theme.Init();

        NetClipboard.Core.L.Init(); // lingua dei testi: prima di costruire qualunque UI
        Application.Run(new TrayContext());
        return 0;
    }
}
