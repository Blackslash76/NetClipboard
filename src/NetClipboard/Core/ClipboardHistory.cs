using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetClipboard.Core.Security;

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

    /// <summary>
    /// Nome del blob con la formattazione (HTML/RTF) del testo, se ce n'era.
    ///
    /// Sta fuori dall'indice perche' l'HTML di Word arriva a megabyte per un
    /// paragrafo: dentro history.dat avrebbe fatto rileggere e riscrivere tutta la
    /// cronologia a ogni copia. L'anteprima in elenco resta il testo, non il markup.
    /// </summary>
    public string? RichFile { get; set; }

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

    /// <summary>
    /// Arrivato da un utente NON accoppiato, tramite "Invia a…". Roba di passaggio:
    /// si segnala nell'elenco e dura poco (vedi <see cref="ClipboardHistory.ExternalLifetime"/>).
    /// </summary>
    public bool FromExternal { get; set; }

    /// <summary>
    /// Hash del contenuto, per la deduplica. Va persistito: senza, dopo un riavvio
    /// l'indice ripartiva vuoto e ricopiare la stessa cosa creava un doppione di
    /// una voce gia' in elenco.
    /// </summary>
    public string Hash { get; set; } = "";

    /// <summary>
    /// Trasferimento ricevuto gia' incollato. La riga resta come traccia di cio'
    /// che e' passato, ma non si riusa: e' un passaggio di consegne, non una
    /// libreria.
    /// </summary>
    public bool Used { get; set; }

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
    /// <summary>
    /// Quanto resta in cronologia un contenuto ricevuto da un utente esterno.
    ///
    /// I file durano quanto il permesso di prelievo concesso dal mittente: scaduto
    /// quello la voce non sarebbe piu' scaricabile, quindi tenerla servirebbe solo
    /// a far cliccare a vuoto. Testo e immagini sono gia' arrivati per intero e
    /// restano piu' a lungo, il tempo di ritrovarli e incollarli.
    /// </summary>
    public static TimeSpan ExternalLifetime(PayloadKind kind) =>
        kind == PayloadKind.Files ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(15);

    /// <summary>Contenuto esterno oltre la sua finestra di vita.</summary>
    public static bool IsExpired(HistoryItem item) =>
        item.FromExternal && DateTime.UtcNow - item.TimestampUtc > ExternalLifetime(item.Kind);

    /// <summary>
    /// Riga non piu' utilizzabile: gia' consumata o scaduta. Resta in elenco,
    /// spenta e marcata, finche' la normale conservazione non la porta via.
    /// </summary>
    public static bool IsSpent(HistoryItem item) => item.Used || IsExpired(item);

    private readonly AppConfig _config;
    private readonly LocalVault _vault;
    private readonly List<HistoryItem> _items = new();
    private readonly Dictionary<string, string> _hashToId = new();
    private readonly Lock _gate = new();

    public event Action? Changed;

    public ClipboardHistory(AppConfig config)
    {
        _config = config;
        _vault = new LocalVault(Path.Combine(config.StateDir, "history.key"));
        Load();
    }

    private string HistoryDir
    {
        get
        {
            var dir = Path.Combine(_config.StateDir, "history");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private string IndexPath => Path.Combine(HistoryDir, "history.dat");

    /// <summary>Indice in chiaro delle versioni fino alla 2.7: si legge, non si scrive piu'.</summary>
    private string LegacyIndexPath => Path.Combine(HistoryDir, "history.json");

    public IReadOnlyList<HistoryItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public HistoryItem? GetById(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    public HistoryItem Add(ClipboardPayload payload, string origin, bool isLocal, bool fromExternal = false)
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
                    existing.FromExternal = fromExternal;
                    // L'hash del testo guarda solo il testo, quindi lo stesso
                    // paragrafo ricopiato in grassetto finisce su questa voce: la
                    // formattazione va aggiornata, non lasciata a quella di prima.
                    // Vale anche al contrario — ricopiarlo in chiaro toglie il
                    // vestito, perche' e' davvero cio' che c'e' in clipboard.
                    if (payload.Kind == PayloadKind.Text) ReplaceRich(existing, payload);
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
                FromExternal = fromExternal,
                Hash = hash,
                Preview = payload.ShortPreview(),
            };

            switch (payload.Kind)
            {
                case PayloadKind.Text:
                    item.Text = payload.Text;
                    if (payload.HasRichText) WriteRich(item, payload);
                    break;
                case PayloadKind.Image:
                    var blobName = item.Id + ".png";
                    File.WriteAllBytes(Path.Combine(HistoryDir, blobName),
                        _vault.Seal(payload.ImagePng ?? Array.Empty<byte>()));
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
        item.OwnerId = offer.OwnerDeviceId;
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

    /// <summary>
    /// Byte in chiaro dell'immagine di una voce, o null se non ci sono piu'.
    ///
    /// Unico punto di lettura dei blob, e per questo pubblico: le miniature della
    /// cronologia leggevano il PNG direttamente da disco con Image.FromFile, e con
    /// i blob cifrati avrebbero smesso di comparire senza dire niente.
    /// </summary>
    public byte[]? ReadBlob(HistoryItem item)
    {
        if (item.BlobFile == null) return null;
        try
        {
            var path = Path.Combine(HistoryDir, item.BlobFile);
            if (!File.Exists(path)) return null;
            return _vault.Open(File.ReadAllBytes(path));
        }
        catch { return null; }
    }

    /// <summary>Formattazione salvata accanto a una voce di testo.</summary>
    private sealed class RichBlob
    {
        public string? Html { get; set; }
        public string? Rtf { get; set; }
    }

    private void ReplaceRich(HistoryItem item, ClipboardPayload payload)
    {
        if (item.RichFile != null)
        {
            try
            {
                var old = Path.Combine(HistoryDir, item.RichFile);
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
            item.RichFile = null;
        }
        if (payload.HasRichText) WriteRich(item, payload);
    }

    private void WriteRich(HistoryItem item, ClipboardPayload payload)
    {
        try
        {
            var name = item.Id + ".rich";
            File.WriteAllBytes(Path.Combine(HistoryDir, name), _vault.Seal(
                JsonSerializer.SerializeToUtf8Bytes(new RichBlob { Html = payload.Html, Rtf = payload.Rtf })));
            item.RichFile = name;
        }
        catch
        {
            // Senza formattazione la voce resta comunque incollabile come testo:
            // non vale la pena far fallire l'inserimento in cronologia.
        }
    }

    private RichBlob? ReadRich(HistoryItem item)
    {
        if (item.RichFile == null) return null;
        try
        {
            var path = Path.Combine(HistoryDir, item.RichFile);
            if (!File.Exists(path)) return null;
            var plain = _vault.Open(File.ReadAllBytes(path));
            return plain == null ? null : JsonSerializer.Deserialize<RichBlob>(plain);
        }
        catch { return null; }
    }

    /// <summary>Solo per testo/immagine: ricostruisce il payload da rimettere in clipboard.</summary>
    public ClipboardPayload? ToPayload(HistoryItem item)
    {
        switch (item.Kind)
        {
            case PayloadKind.Text:
                var rich = ReadRich(item);
                return rich == null
                    ? ClipboardPayload.FromText(item.Text ?? "")
                    : ClipboardPayload.FromRichText(item.Text ?? "", rich.Html, rich.Rtf);
            case PayloadKind.Image:
                var png = ReadBlob(item);
                return png != null ? ClipboardPayload.FromImage(png) : null;
            default:
                return null; // i file si materializzano via fetch, non da qui
        }
    }

    /// <summary>Registra i percorsi materializzati (dopo il download) su una voce file.</summary>
    /// <summary>Segna un trasferimento come gia' incollato.</summary>
    public void MarkUsed(string id)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null || item.Used) return;
            item.Used = true;
            Persist();
        }
        Changed?.Invoke();
    }

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
        var now = DateTime.UtcNow;
        var days = _config.HistoryMaxAgeDays;

        // Gli esterni scadono comunque, anche con la conservazione illimitata:
        // e' contenuto di passaggio, non cronologia propria.
        // I contenuti esterni scaduti NON si tolgono: restano in elenco spenti e
        // marcati, cosi' si capisce cosa e' passato e perche' non e' piu' usabile.
        // Se ne occupa la normale conservazione, come per tutto il resto.
        bool Expired(HistoryItem i) =>
            !i.Pinned && days > 0 && i.TimestampUtc < now.AddDays(-days);

        var changed = false;
        lock (_gate)
        {
            foreach (var it in _items.Where(Expired).ToList())
            {
                _items.Remove(it);
                Deindex(it);
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
            Deindex(item);
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

    /// <summary>Toglie la voce dall'indice di deduplica, se e' lei a occuparne la chiave.</summary>
    private void Deindex(HistoryItem item)
    {
        if (item.Hash.Length == 0) return;
        if (_hashToId.TryGetValue(item.Hash, out var id) && id == item.Id)
            _hashToId.Remove(item.Hash);
    }

    private void TrimUnlocked()
    {
        var max = Math.Max(5, _config.HistorySize);
        while (_items.Count > max)
        {
            var victim = _items.LastOrDefault(i => !i.Pinned);
            if (victim == null) break;
            _items.Remove(victim);
            Deindex(victim);
            DeleteBlob(victim);
        }
    }

    private void DeleteBlob(HistoryItem item)
    {
        foreach (var name in new[] { item.BlobFile, item.RichFile })
        {
            try
            {
                if (name == null) continue;
                var p = Path.Combine(HistoryDir, name);
                if (File.Exists(p)) File.Delete(p);
            }
            catch { }
        }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllBytes(IndexPath, _vault.Seal(JsonSerializer.SerializeToUtf8Bytes(_items)));
            // La copia in chiaro non deve sopravvivere alla prima scrittura: sarebbe
            // l'elenco di tutto il testo passato, fermo li' per sempre.
            if (File.Exists(LegacyIndexPath)) File.Delete(LegacyIndexPath);
        }
        catch { }
    }

    private void Load()
    {
        var fromLegacy = !File.Exists(IndexPath) && File.Exists(LegacyIndexPath);
        try
        {
            var json = ReadIndexJson();
            if (json == null) return;
            var items = JsonSerializer.Deserialize<List<HistoryItem>>(json);
            if (items == null) return;
            _items.AddRange(items);

            // Indice ricostruito dal disco: senza, la deduplica ripartiva da zero a
            // ogni avvio. Le voci salvate da versioni precedenti non hanno l'hash e
            // rientreranno nell'indice alla prima ricopiatura.
            foreach (var it in _items)
                if (it.Hash.Length > 0)
                    _hashToId[it.Hash] = it.Id;
        }
        catch { }
        MigratePlainBlobs();
        // Si riscrive subito, cifrato, invece di aspettare la prima copia: finche'
        // l'indice in chiaro resta li' il problema che si voleva chiudere e' aperto,
        // e su un'app che sta ferma potrebbe restarci per giorni.
        if (fromLegacy) Persist();
        PurgeExpired();
    }

    /// <summary>
    /// Indice, in chiaro, da dove si trova.
    ///
    /// Migrazione dalle versioni che non cifravano: si legge il vecchio
    /// <c>history.json</c> e lo si tratta come valido. Non si scarta, perche' e'
    /// cronologia dell'utente e buttarla via in silenzio sarebbe peggio del male;
    /// sparisce da sola alla prima scrittura, che avviene cifrata.
    ///
    /// Un indice cifrato che non si apre, invece, si scarta: vuol dire che la
    /// chiave non e' piu' quella, e non c'e' modo di recuperarlo.
    /// </summary>
    private string? ReadIndexJson()
    {
        if (File.Exists(IndexPath))
        {
            var plain = _vault.Open(File.ReadAllBytes(IndexPath));
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        return File.Exists(LegacyIndexPath) ? File.ReadAllText(LegacyIndexPath) : null;
    }

    /// <summary>
    /// Ricifra i PNG lasciati in chiaro dalle versioni precedenti. Si fa una volta
    /// sola all'avvio: <see cref="LocalVault.Open"/> saprebbe comunque rileggerli,
    /// ma finche' restano cosi' il problema che si voleva chiudere e' ancora li'.
    /// </summary>
    private void MigratePlainBlobs()
    {
        foreach (var it in _items.Where(i => i.BlobFile != null))
        {
            try
            {
                var path = Path.Combine(HistoryDir, it.BlobFile!);
                if (!File.Exists(path)) continue;
                var bytes = File.ReadAllBytes(path);
                if (LocalVault.IsSealed(bytes)) continue;
                File.WriteAllBytes(path, _vault.Seal(bytes));
            }
            catch { }
        }
    }
}
