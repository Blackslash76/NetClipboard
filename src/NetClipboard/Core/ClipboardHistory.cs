using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetClipboard.Core;

/// <summary>Una voce della cronologia clipboard unificata (locale + peer).</summary>
public sealed class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public PayloadKind Kind { get; set; }
    public string Origin { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    public string Preview { get; set; } = "";

    // Testo
    public string? Text { get; set; }

    // Immagine: nome del blob PNG in %AppData%\NetClipboard\history
    public string? BlobFile { get; set; }

    // File/cartelle (offer)
    public string? OfferId { get; set; }
    public string? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public bool IsLocalOffer { get; set; }
    public int FileCount { get; set; }
    public int DirCount { get; set; }
    public long TotalSize { get; set; }
    public List<string>? TopNames { get; set; }

    /// <summary>Percorsi radice locali: originali (host) o scaricati (destinatario). Null se non ancora materializzato.</summary>
    public List<string>? LocalRootPaths { get; set; }

    [JsonIgnore]
    public bool IsLocal { get; set; }
}

/// <summary>
/// Cronologia condivisa tra i device: raccoglie ogni contenuto che passa dalla
/// clipboard, locale o ricevuto dai peer, con de-duplica per hash e limite di
/// dimensione. Persistita in %AppData%\NetClipboard\history.
/// </summary>
public sealed class ClipboardHistory
{
    private readonly AppConfig _config;
    private readonly List<HistoryItem> _items = new();
    private readonly Dictionary<string, string> _hashToId = new();
    private readonly Lock _gate = new();

    public event Action? Changed;

    public ClipboardHistory(AppConfig config)
    {
        _config = config;
        Load();
    }

    private static string HistoryDir
    {
        get
        {
            var dir = Path.Combine(AppConfig.AppDataDir, "history");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string IndexPath => Path.Combine(HistoryDir, "history.json");

    public IReadOnlyList<HistoryItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public HistoryItem? GetById(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    public HistoryItem Add(ClipboardPayload payload, string origin, bool isLocal)
    {
        PurgeExpired();
        var hash = payload.ContentHash();
        lock (_gate)
        {
            if (_hashToId.TryGetValue(hash, out var existingId))
            {
                var existing = _items.FirstOrDefault(i => i.Id == existingId);
                if (existing != null)
                {
                    _items.Remove(existing);
                    existing.TimestampUtc = DateTime.UtcNow;
                    existing.Origin = origin;
                    existing.IsLocal = isLocal;
                    _items.Insert(0, existing);
                    Persist();
                    Changed?.Invoke();
                    return existing;
                }
            }

            var item = new HistoryItem
            {
                Kind = payload.Kind,
                Origin = origin,
                IsLocal = isLocal,
                Preview = payload.ShortPreview(),
            };

            switch (payload.Kind)
            {
                case PayloadKind.Text:
                    item.Text = payload.Text;
                    break;
                case PayloadKind.Image:
                    var blobName = item.Id + ".png";
                    File.WriteAllBytes(Path.Combine(HistoryDir, blobName), payload.ImagePng ?? Array.Empty<byte>());
                    item.BlobFile = blobName;
                    break;
                case PayloadKind.Files:
                    FillFromOffer(item, payload.Offer!, isLocal);
                    break;
            }

            _items.Insert(0, item);
            _hashToId[hash] = item.Id;
            TrimUnlocked();
            Persist();
            Changed?.Invoke();
            return item;
        }
    }

    private static void FillFromOffer(HistoryItem item, FileOffer offer, bool isLocal)
    {
        item.OfferId = offer.OfferId.ToString("N");
        item.OwnerId = offer.OwnerId.ToString("N");
        item.OwnerName = offer.OwnerName;
        item.IsLocalOffer = isLocal;
        item.FileCount = offer.FileCount;
        item.DirCount = offer.DirCount;
        item.TotalSize = offer.TotalSize;
        item.TopNames = offer.TopLevelNames.ToList();

        // Se e' un'offerta nostra, conosciamo gia' i percorsi reali.
        if (isLocal && offer.RootParents != null)
        {
            item.LocalRootPaths = offer.Entries
                .Where(e => !e.RelativePath.Contains('/'))
                .Select(offer.ResolveLocal)
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();
        }
    }

    /// <summary>Solo per testo/immagine: ricostruisce il payload da rimettere in clipboard.</summary>
    public ClipboardPayload? ToPayload(HistoryItem item)
    {
        switch (item.Kind)
        {
            case PayloadKind.Text:
                return ClipboardPayload.FromText(item.Text ?? "");
            case PayloadKind.Image:
                if (item.BlobFile == null)
                    return null;
                var path = Path.Combine(HistoryDir, item.BlobFile);
                return File.Exists(path) ? ClipboardPayload.FromImage(File.ReadAllBytes(path)) : null;
            default:
                return null; // i file si materializzano via fetch, non da qui
        }
    }

    /// <summary>Registra i percorsi materializzati (dopo il download) su una voce file.</summary>
    public void SetMaterialized(string id, List<string> rootPaths)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                item.LocalRootPaths = rootPaths;
                Persist();
            }
        }
    }

    /// <summary>Rimuove gli elementi non fissati più vecchi del limite di età configurato.</summary>
    public void PurgeExpired()
    {
        var days = _config.HistoryMaxAgeDays;
        if (days <= 0)
            return;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var changed = false;
        lock (_gate)
        {
            foreach (var it in _items.Where(i => !i.Pinned && i.TimestampUtc < cutoff).ToList())
            {
                _items.Remove(it);
                var key = _hashToId.FirstOrDefault(kv => kv.Value == it.Id).Key;
                if (key != null) _hashToId.Remove(key);
                DeleteBlob(it);
                changed = true;
            }
            if (changed) Persist();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Elimina le cartelle di file ricevuti più vecchie del limite di età.</summary>
    public static void CleanupReceived(int maxAgeDays)
    {
        if (maxAgeDays <= 0)
            return;
        try
        {
            var dir = Path.Combine(AppConfig.AppDataDir, "received");
            if (!Directory.Exists(dir))
                return;
            var cutoff = DateTime.Now.AddDays(-maxAgeDays);
            foreach (var sub in Directory.GetDirectories(dir))
            {
                try { if (Directory.GetLastWriteTime(sub) < cutoff) Directory.Delete(sub, true); }
                catch { }
            }
        }
        catch { }
    }

    public void TogglePin(string id)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null) { item.Pinned = !item.Pinned; Persist(); Changed?.Invoke(); }
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return;
            _items.Remove(item);
            var key = _hashToId.FirstOrDefault(kv => kv.Value == id).Key;
            if (key != null) _hashToId.Remove(key);
            DeleteBlob(item);
            Persist();
            Changed?.Invoke();
        }
    }

    public void Clear(bool keepPinned = true)
    {
        lock (_gate)
        {
            foreach (var i in _items.Where(i => !keepPinned || !i.Pinned).ToList())
            {
                _items.Remove(i);
                DeleteBlob(i);
            }
            _hashToId.Clear();
            Persist();
            Changed?.Invoke();
        }
    }

    private void TrimUnlocked()
    {
        var max = Math.Max(5, _config.HistorySize);
        while (_items.Count > max)
        {
            var victim = _items.LastOrDefault(i => !i.Pinned);
            if (victim == null) break;
            _items.Remove(victim);
            DeleteBlob(victim);
        }
    }

    private static void DeleteBlob(HistoryItem item)
    {
        try
        {
            if (item.BlobFile != null)
            {
                var p = Path.Combine(HistoryDir, item.BlobFile);
                if (File.Exists(p)) File.Delete(p);
            }
        }
        catch { }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(_items));
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var items = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(IndexPath));
            if (items != null) _items.AddRange(items);
        }
        catch { }
        PurgeExpired();
    }
}
