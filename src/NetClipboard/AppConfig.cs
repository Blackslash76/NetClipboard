using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetClipboard;

/// <summary>
/// Configurazione persistente dell'applicazione, salvata in
/// %AppData%\NetClipboard\config.json. L'identità e la fiducia stanno altrove
/// (identity.key, trusted.json): qui solo preferenze e cache.
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// Come questo PC si presenta agli altri. NON e' piu' modificabile: e' il nome
    /// della macchina e basta. Rinominarsi confondeva chi riceve, che vedeva un
    /// nome che non corrisponde a nessun computer della rete; per chiamare gli
    /// altri come si vuole c'e' <see cref="DeviceLabels"/>, che resta locale.
    /// </summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Etichette scelte da noi per gli altri dispositivi (DeviceId -> nome).
    /// Vale solo su questo PC: nessuno la vede e nessuno la puo' cambiare da fuori,
    /// e proprio per questo e' l'unico nome di cui ci si puo' fidare in elenco.
    /// </summary>
    public Dictionary<string, string> DeviceLabels { get; set; } = new();
    public int Port { get; set; } = 45654;

    public bool ShareText { get; set; } = true;
    public bool ShareImages { get; set; } = true;
    public bool ShareFiles { get; set; } = true;

    /// <summary>Dimensione massima (MB) del payload trasferito (soprattutto file).</summary>
    public int MaxTransferMb { get; set; } = 50;

    /// <summary>Numero massimo di elementi tenuti nella cronologia.</summary>
    public int HistorySize { get; set; } = 30;

    /// <summary>
    /// Righe mostrate insieme nel pannello della cronologia, e quindi la sua
    /// altezza. Poche righe rendono il pannello un menu che non copre lo schermo;
    /// alzarlo serve a chi tiene molta roba a portata di mano.
    /// </summary>
    public int HistoryVisibleRows { get; set; } = 4;

    /// <summary>Estremi accettati per <see cref="HistoryVisibleRows"/>.</summary>
    public const int MinVisibleRows = 3;
    public const int MaxVisibleRows = 8;

    /// <summary>Giorni di conservazione degli elementi non fissati (0 = illimitato).</summary>
    public int HistoryMaxAgeDays { get; set; } = 7;

    /// <summary>Stato iniziale del toggle di condivisione (modalita ibrida).</summary>
    public bool StartSharingEnabled { get; set; } = true;

    public bool StartWithWindows { get; set; } = false;

    /// <summary>Voce di NetClipboard nel menu "Invia a" di Windows.</summary>
    public bool SendToMenu { get; set; } = false;

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

    // ----- Identità aziendale (Microsoft Entra ID) -----

    /// <summary>
    /// ID dell'applicazione registrata nel tenant (GUID). Vuoto = funzione
    /// disattivata: l'app lavora con la sola identità di dispositivo, come prima.
    /// </summary>
    public string EntraClientId { get; set; } = "";

    /// <summary>
    /// Tenant a cui rivolgersi: un GUID (o dominio) per limitarsi alla propria
    /// organizzazione, "organizations" per un qualsiasi account aziendale.
    /// </summary>
    public string EntraTenant { get; set; } = "organizations";

    /// <summary>Tenta l'accesso silenzioso all'avvio (nessuna finestra: o riesce o si prosegue senza).</summary>
    public bool EntraSignInAtStartup { get; set; } = true;

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
                {
                    // Il nome proprio non si sceglie piu': se in un config vecchio
                    // c'era un nome inventato, torna quello della macchina.
                    cfg.DisplayName = Environment.MachineName;
                    return cfg;
                }
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
}
