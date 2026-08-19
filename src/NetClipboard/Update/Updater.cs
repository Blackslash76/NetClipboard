using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetClipboard.Core;

namespace NetClipboard.Update;

public sealed record UpdateInfo(Version Version, string ExeUrl, string Sha256, string Note);

/// <summary>
/// Esito del tentativo di sostituire l'eseguibile. <see cref="Declined"/> esiste
/// per non dire "installazione fallita" a chi ha semplicemente risposto no alla
/// richiesta di amministratore: non e' un guasto, ed e' un caso che da Program
/// Files si ripresenta a ogni aggiornamento.
/// </summary>
public enum UpdateApply { Started, Declined, Failed }

/// <summary>
/// Auto-update da GitHub Releases con firma crittografica.
///
/// Flusso: scarica un piccolo manifest.json (URL stabile
/// .../releases/latest/download/manifest.json), verifica la FIRMA con la chiave
/// pubblica release incorporata, confronta la versione, scarica l'exe, verifica
/// SHA-256, e su conferma dell'utente sostituisce l'eseguibile e riavvia.
///
/// La firma è la difesa chiave: solo chi possiede la chiave privata release può
/// produrre un update accettato, anche se GitHub/HTTPS fossero compromessi.
/// </summary>
public static class Updater
{
    // Chiave PUBBLICA di firma release (SubjectPublicKeyInfo, base64).
    // La chiave PRIVATA la tiene lo sviluppatore e serve a firmare ogni release.
    public const string ReleasePublicKeyB64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE7YXqpiZAZ7RDgDKU+UQTyPeOVJAV0K4wb92/+FM0DSwYc/0anghtENqz9/fCTGPYqLWaDRA/L7/O/bmhnhaQeQ==";

    /// <summary>URL del manifest fissato nell'eseguibile (usato se non c'è un override in Impostazioni).</summary>
    public const string DefaultManifestUrl =
        "https://github.com/Blackslash76/NetClipboard/releases/latest/download/manifest.json";

    private const string SigContext = "netclip-update-v1";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("NetClipboard-Updater");
        return h;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static bool IsConfigured(string? manifestUrl) =>
        !string.IsNullOrWhiteSpace(manifestUrl)
        && ReleasePublicKeyB64 != "REPLACE_WITH_RELEASE_PUBLIC_KEY";

    // ----- Check (rete) -----

    public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct)
    {
        if (!IsConfigured(manifestUrl))
            return null;
        try
        {
            var json = await Http.GetStringAsync(manifestUrl, ct);
            if (!VerifyManifestJson(json, ReleasePublicKeyB64, out var info, out var reason))
            {
                Log.Write($"[Update] manifest scartato: {reason}");
                return null;
            }
            if (info!.Version <= CurrentVersion)
            {
                Log.Write($"[Update] già aggiornato (locale {CurrentVersion}, remoto {info.Version})");
                return null;
            }
            Log.Write($"[Update] disponibile v{info.Version} (locale {CurrentVersion})");
            return info;
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] check fallito: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> DownloadAsync(UpdateInfo info, CancellationToken ct)
    {
        try
        {
            // Cartella nuova e con nome casuale a ogni download, non un percorso
            // prevedibile in %TEMP%: fra la verifica dello SHA-256 e la sostituzione
            // dell'eseguibile c'e' una finestra, e un percorso che si puo' indovinare
            // e' un invito a infilarcisi dentro.
            var dir = Directory.CreateTempSubdirectory("netclip-update-");
            var tmp = Path.Combine(dir.FullName, $"NetClipboard.{info.Version}.exe");
            using (var resp = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tmp, ct)));
            if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("[Update] SHA-256 non combacia: scarto il download");
                try { dir.Delete(recursive: true); } catch { }
                return null;
            }
            Log.Write($"[Update] scaricato e verificato v{info.Version}");
            return tmp;
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] download fallito: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Argomento della modalita' che sostituisce l'eseguibile con i diritti di
    /// amministratore: <c>--apply-update &lt;exe da sostituire&gt; &lt;pid da attendere&gt;</c>.
    /// </summary>
    public const string ApplyArgument = "--apply-update";

    /// <summary>Quanto si aspetta che il processo da aggiornare lasci l'eseguibile.</summary>
    private static readonly TimeSpan ParentExitWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Sostituisce l'eseguibile in uso e riavvia. Il chiamante deve poi uscire.
    ///
    /// Da <c>C:\Program Files</c> un processo non elevato non puo' toccare il
    /// proprio eseguibile: in quel caso non si fallisce, si chiede l'elevazione e
    /// si lascia fare allo stesso binario appena scaricato (vedi <see cref="RunApplyHelper"/>).
    /// </summary>
    public static UpdateApply ApplyAndRestart(string newExe)
    {
        var current = Environment.ProcessPath!;
        return CanWriteBeside(current) ? SwapInPlace(current, newExe) : SwapElevated(current, newExe);
    }

    /// <summary>Si prova a scrivere davvero: i permessi effettivi non si deducono dal percorso.</summary>
    private static bool CanWriteBeside(string exePath)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(exePath)!, $".netclip-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static UpdateApply SwapInPlace(string current, string newExe)
    {
        try
        {
            var old = current + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(current, old);          // rinominare un exe in uso è consentito
            File.Move(newExe, current);       // il nuovo prende il posto
            try { Directory.Delete(Path.GetDirectoryName(newExe)!); } catch { }
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
            Log.Write("[Update] applicato, riavvio in corso");
            return UpdateApply.Started;
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] applicazione fallita: {ex.Message}");
            return UpdateApply.Failed;
        }
    }

    /// <summary>
    /// Rilancia elevato l'eseguibile <b>appena scaricato</b> perche' faccia lui lo
    /// scambio. E' gia' stato verificato contro lo SHA-256 firmato, quindi non si
    /// sta dando l'amministratore a qualcosa di arrivato senza controlli.
    /// </summary>
    private static UpdateApply SwapElevated(string current, string newExe)
    {
        try
        {
            var psi = new ProcessStartInfo(newExe)
            {
                UseShellExecute = true,   // obbligatorio per "runas"
                Verb = "runas",
            };
            psi.ArgumentList.Add(ApplyArgument);
            psi.ArgumentList.Add(current);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Process.Start(psi);
            Log.Write("[Update] cartella non scrivibile: scambio delegato a un processo elevato");
            return UpdateApply.Started;   // il chiamante esce: da qui in poi tocca all'helper
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Write("[Update] elevazione rifiutata dall'utente");
            return UpdateApply.Declined;
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] elevazione fallita: {ex.Message}");
            return UpdateApply.Failed;
        }
    }

    /// <summary>
    /// Modalita' helper, eseguita elevata dal binario nuovo: aspetta che il vecchio
    /// processo esca, prende il suo posto e lo riavvia <b>senza</b> privilegi.
    ///
    /// Il riavvio passa da Explorer proprio per questo: un processo lanciato da uno
    /// elevato eredita i suoi privilegi, e NetClipboard non deve girare da
    /// amministratore — legge la clipboard e sta in ascolto sulla rete, sarebbe un
    /// regalo a chiunque trovasse un modo di parlargli.
    /// </summary>
    public static int RunApplyHelper(string target, int parentPid)
    {
        try
        {
            WaitForExit(parentPid);

            var self = Environment.ProcessPath!;
            var old = target + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }

            var moved = false;
            try
            {
                if (File.Exists(target)) { File.Move(target, old); moved = true; }
                File.Copy(self, target, overwrite: true);   // non si puo' spostare: e' l'exe in esecuzione
            }
            catch (Exception ex)
            {
                // Meglio l'applicazione di prima che nessuna applicazione: se la
                // copia non riesce, il vecchio eseguibile torna al suo posto.
                Log.Write($"[Update] scambio elevato fallito: {ex.Message}");
                if (moved && !File.Exists(target))
                    try { File.Move(old, target); } catch { }
                return 1;
            }

            RestartUnelevated(target);
            Log.Write("[Update] applicato con elevazione, riavvio in corso");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] helper fallito: {ex.Message}");
            return 1;
        }
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            parent.WaitForExit((int)ParentExitWait.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // gia' uscito fra la richiesta di elevazione e adesso: e' il caso normale
        }
    }

    /// <summary>
    /// Riavvia scaricando i privilegi. Explorer gira a integrita' media: cio' che
    /// avvia parte come l'utente, non come l'amministratore che siamo adesso.
    /// </summary>
    private static void RestartUnelevated(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] riavvio non riuscito, si aprira' al prossimo accesso: {ex.Message}");
        }
    }

    /// <summary>Rimuove l'eventuale .old lasciato da un aggiornamento precedente.</summary>
    public static void CleanupOld()
    {
        try
        {
            var old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }

        // Lo scambio elevato COPIA il nuovo eseguibile invece di spostarlo — non
        // puo' spostare se stesso mentre gira — quindi la cartella di scarico resta
        // li' con dentro un eseguibile intero. La toglie chi riparte.
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "netclip-update-*"))
                try { Directory.Delete(dir, recursive: true); } catch { }
        }
        catch { }
    }

    // ----- Verifica manifest (pura, testabile offline) -----

    public static bool VerifyManifestJson(string json, string publicKeyB64, out UpdateInfo? info, out string reason)
    {
        info = null;
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(json, JsonOpts);
            if (m == null || string.IsNullOrEmpty(m.Version) || string.IsNullOrEmpty(m.Sha256)
                || string.IsNullOrEmpty(m.Signature) || string.IsNullOrEmpty(m.ExeUrl))
            {
                reason = "campi mancanti";
                return false;
            }
            if (!Version.TryParse(m.Version, out var v))
            {
                reason = "versione non valida";
                return false;
            }
            var msg = Encoding.UTF8.GetBytes(SignedMessage(m.Version, m.Sha256));
            var sig = Convert.FromBase64String(m.Signature);
            var pub = Convert.FromBase64String(publicKeyB64);
            if (!DeviceIdentityVerify(pub, msg, sig))
            {
                reason = "firma non valida";
                return false;
            }
            info = new UpdateInfo(v, m.ExeUrl, m.Sha256, m.Note ?? "");
            reason = "ok";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    // ----- Lato sviluppatore: generazione chiavi e firma release -----

    public static (string PrivateB64, string PublicB64) GenerateKeypair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ec.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
    }

    public static string BuildManifestJson(string exePath, string version, string privateKeyB64, string exeUrl, string note)
    {
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath)));
        var msg = Encoding.UTF8.GetBytes(SignedMessage(version, sha));
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        var sig = Convert.ToBase64String(ec.SignData(msg, HashAlgorithmName.SHA256));
        var m = new Manifest { Version = version, ExeUrl = exeUrl, Sha256 = sha, Signature = sig, Note = note };
        return JsonSerializer.Serialize(m, JsonOpts);
    }

    // ----- interni -----

    private static string SignedMessage(string version, string sha256) =>
        $"{SigContext}|{version}|{sha256.ToLowerInvariant()}";

    private static bool DeviceIdentityVerify(byte[] publicKeyDer, byte[] data, byte[] signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
            return ec.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class Manifest
    {
        public string Version { get; set; } = "";
        public string ExeUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Signature { get; set; } = "";
        public string? Note { get; set; }
    }
}
