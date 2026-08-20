using System.IO;
using System.Text.Json;

namespace NetClipboard.Core.Security;

public sealed class TrustedDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Chiave pubblica (SubjectPublicKeyInfo DER) in base64: il "pin".</summary>
    public string PublicKeyB64 { get; set; } = "";

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Se true, questo dispositivo può "presentare" (introdurre) altri fidati.</summary>
    public bool Introducer { get; set; } = true;

    public byte[] PublicKeyDer => Convert.FromBase64String(PublicKeyB64);
}

/// <summary>
/// Elenco dei dispositivi fidati (chiavi pubbliche "pinnate"). Il gruppo è
/// definito da questa lista, non da una password condivisa: aggiungere/revocare
/// un dispositivo è una modifica locale, propagabile via introduzione.
/// </summary>
public sealed class TrustStore
{
    private readonly string _path;
    private readonly string _revokedPath;
    private readonly Dictionary<string, TrustedDevice> _devices = new();

    /// <summary>
    /// Dispositivi revocati a mano: lapidi permanenti.
    ///
    /// Senza questo elenco la revoca non reggeva: il gossip della mesh reintroduce
    /// i dispositivi annunciati dagli altri fidati, e siccome il ping gira ogni tre
    /// secondi bastava attendere per ritrovarsi il revocato di nuovo in lista.
    /// Si azzera solo con un pairing esplicito, cioe' quando l'utente riconferma
    /// di persona il codice a sei cifre.
    /// </summary>
    private readonly HashSet<string> _revoked = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();

    public event Action? Changed;

    public TrustStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppConfig.AppDataDir, "trusted.json");
        // File separato: cosi' trusted.json resta leggibile dalle versioni precedenti.
        _revokedPath = Path.Combine(Path.GetDirectoryName(_path)!, "revoked.json");
        Load();
        LoadRevoked();
    }

    /// <summary>True se il dispositivo e' stato revocato a mano e non va rifidato da solo.</summary>
    public bool IsRevoked(string deviceId)
    {
        lock (_gate)
            return _revoked.Contains(deviceId);
    }

    public bool IsTrusted(string deviceId)
    {
        lock (_gate)
            return _devices.ContainsKey(deviceId);
    }

    /// <summary>Verifica che l'ID corrisponda alla chiave pinnata (anti-sostituzione).</summary>
    public bool Matches(string deviceId, byte[] publicKeyDer)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var d))
                return false;
            return d.PublicKeyB64 == Convert.ToBase64String(publicKeyDer);
        }
    }

    public TrustedDevice? Get(string deviceId)
    {
        lock (_gate)
            return _devices.GetValueOrDefault(deviceId);
    }

    public IReadOnlyList<TrustedDevice> All
    {
        get { lock (_gate) return _devices.Values.ToList(); }
    }

    /// <summary>
    /// Concede la fiducia. E' un atto esplicito, quindi cancella un'eventuale
    /// revoca precedente: il chiamante deve aver gia' scartato i revocati se
    /// l'origine e' automatica (vedi ProcessGossip).
    /// </summary>
    public void Trust(string deviceId, string name, byte[] publicKeyDer, bool introducer = true)
    {
        lock (_gate)
        {
            if (_revoked.Remove(deviceId)) PersistRevoked();
            _devices[deviceId] = new TrustedDevice
            {
                DeviceId = deviceId,
                Name = name,
                PublicKeyB64 = Convert.ToBase64String(publicKeyDer),
                Introducer = introducer,
            };
            Persist();
        }
        Changed?.Invoke();
    }

    public void Revoke(string deviceId)
    {
        lock (_gate)
        {
            _devices.Remove(deviceId);
            Persist();
            // La lapide si scrive comunque, anche se il dispositivo non era in
            // elenco: puo' essere stato tolto un attimo prima e stare per tornare
            // dal gossip di un altro peer.
            if (_revoked.Add(deviceId)) PersistRevoked();
        }
        Changed?.Invoke();
    }

    /// <summary>Dimentica la revoca senza concedere fiducia: il dispositivo torna accoppiabile a mano.</summary>
    public void ClearRevocation(string deviceId)
    {
        lock (_gate)
        {
            if (!_revoked.Remove(deviceId)) return;
            PersistRevoked();
        }
        Changed?.Invoke();
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_devices.Values.ToList())); }
        catch { }
    }

    private void PersistRevoked()
    {
        try { File.WriteAllText(_revokedPath, JsonSerializer.Serialize(_revoked.ToList())); }
        catch { }
    }

    private void LoadRevoked()
    {
        try
        {
            if (!File.Exists(_revokedPath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_revokedPath));
            if (list == null) return;
            foreach (var id in list)
                if (!string.IsNullOrEmpty(id)) _revoked.Add(id);
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(_path));
            if (list == null) return;
            foreach (var d in list)
                if (!string.IsNullOrEmpty(d.DeviceId))
                    _devices[d.DeviceId] = d;
        }
        catch { }
    }
}
