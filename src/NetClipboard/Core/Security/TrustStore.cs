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
    private readonly Dictionary<string, TrustedDevice> _devices = new();
    private readonly Lock _gate = new();

    public event Action? Changed;

    public TrustStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppConfig.AppDataDir, "trusted.json");
        Load();
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

    public void Trust(string deviceId, string name, byte[] publicKeyDer, bool introducer = true)
    {
        lock (_gate)
        {
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
        bool removed;
        lock (_gate)
        {
            removed = _devices.Remove(deviceId);
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke();
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_devices.Values.ToList())); }
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
