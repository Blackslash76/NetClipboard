using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetClipboard;

/// <summary>
/// Configurazione persistente dell'applicazione, salvata in
/// %AppData%\NetClipboard\config.json. La password non viene mai salvata in
/// chiaro: si usa DPAPI (ProtectedData) legata all'utente Windows corrente.
/// </summary>
public sealed class AppConfig
{
    public Guid InstanceId { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = Environment.MachineName;
    public int Port { get; set; } = 45654;

    public bool ShareText { get; set; } = true;
    public bool ShareImages { get; set; } = true;
    public bool ShareFiles { get; set; } = true;

    /// <summary>Dimensione massima (MB) del payload trasferito (soprattutto file).</summary>
    public int MaxTransferMb { get; set; } = 50;

    /// <summary>Numero massimo di elementi tenuti nella cronologia.</summary>
    public int HistorySize { get; set; } = 30;

    /// <summary>Giorni di conservazione degli elementi non fissati (0 = illimitato).</summary>
    public int HistoryMaxAgeDays { get; set; } = 7;

    /// <summary>Stato iniziale del toggle di condivisione (modalita ibrida).</summary>
    public bool StartSharingEnabled { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>URL del manifest di aggiornamento (es. .../releases/latest/download/manifest.json). Vuoto = disattivato.</summary>
    public string UpdateManifestUrl { get; set; } = "";

    /// <summary>Controllo automatico degli aggiornamenti all'avvio e periodico.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>IP dei peer da contattare in unicast quando il broadcast è bloccato.</summary>
    public List<string> ManualPeers { get; set; } = new();

    /// <summary>Scansione automatica della subnet SOLO al primo avvio (poi bootstrap da cache + gossip).</summary>
    public bool AutoScanDiscovery { get; set; } = true;

    /// <summary>Cache degli IP dei peer già visti: al riavvio si ripingano (niente scan).</summary>
    public List<string> KnownPeerIps { get; set; } = new();

    /// <summary>Password protetta con DPAPI (base64). Non leggibile fuori dall'utente.</summary>
    public string ProtectedPassword { get; set; } = "";

    // ----- Non serializzato: la password in chiaro vive solo in memoria -----

    [JsonIgnore]
    public string Password
    {
        get => Unprotect(ProtectedPassword);
        set => ProtectedPassword = Protect(value);
    }

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(ProtectedPassword);

    // ----- Percorsi -----

    public static string AppDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NetClipboard");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string ConfigPath => Path.Combine(AppDataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ----- Load / Save -----

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                if (cfg != null)
                    return cfg;
            }
        }
        catch
        {
            // config corrotta: si riparte da default
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // best effort
        }
    }

    // ----- DPAPI helper -----

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";
        var bytes = Encoding.UTF8.GetBytes(plain);
        var prot = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(prot);
    }

    private static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64))
            return "";
        try
        {
            var prot = Convert.FromBase64String(protectedB64);
            var bytes = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
