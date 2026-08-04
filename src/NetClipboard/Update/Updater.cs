using System.Diagnostics;
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
            var tmp = Path.Combine(Path.GetTempPath(), $"NetClipboard.update.{info.Version}.exe");
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
                try { File.Delete(tmp); } catch { }
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

    /// <summary>Sostituisce l'eseguibile in uso e riavvia. Il chiamante deve poi uscire.</summary>
    public static bool ApplyAndRestart(string newExe)
    {
        try
        {
            var current = Environment.ProcessPath!;
            var old = current + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(current, old);          // rinominare un exe in uso è consentito
            File.Move(newExe, current);       // il nuovo prende il posto
            Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
            Log.Write("[Update] applicato, riavvio in corso");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[Update] applicazione fallita: {ex.Message}");
            return false;
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
