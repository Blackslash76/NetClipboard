using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace NetClipboard.Core;

/// <summary>
/// Registro lato-host delle offerte file attive: mappa OfferId -> percorsi reali
/// locali, cosi da poter servire i byte quando un peer li chiede (fetch on paste).
/// Persistito su disco per sopravvivere ai riavvii.
/// </summary>
public sealed class OfferStore
{
    private const int MaxOffers = 200;

    private readonly ConcurrentDictionary<Guid, FileOffer> _offers = new();
    private readonly Lock _gate = new();

    private readonly string _path;

    /// <summary>
    /// La cartella arriva dalla configurazione e non da <see cref="AppConfig.AppDataDir"/>:
    /// il banco di prova end-to-end tiene due istanze nello stesso processo, e con
    /// un percorso solo si sovrascriverebbero le offerte a vicenda.
    /// </summary>
    public OfferStore(AppConfig config)
    {
        _path = Path.Combine(config.StateDir, "offers.json");
        Load();
    }

    private string StorePath => _path;

    public void Register(FileOffer offer)
    {
        _offers[offer.OfferId] = offer;
        lock (_gate)
        {
            PruneUnlocked();
            Persist();
        }
    }

    public FileOffer? Get(Guid offerId) => _offers.GetValueOrDefault(offerId);

    private void PruneUnlocked()
    {
        if (_offers.Count <= MaxOffers)
            return;
        // Rimuove le piu' vecchie (per OfferId non abbiamo timestamp: usiamo ordine di
        // inserimento approssimato tramite un semplice troncamento sul conteggio).
        var excess = _offers.Count - MaxOffers;
        foreach (var key in _offers.Keys.Take(excess).ToList())
            _offers.TryRemove(key, out _);
    }

    // ----- Persistenza -----

    private sealed class OfferDto
    {
        public Guid OfferId { get; set; }
        public string OwnerName { get; set; } = "";
        public List<string> RootParents { get; set; } = new();
        public List<FileEntry> Entries { get; set; } = new();
    }

    private void Persist()
    {
        try
        {
            var dtos = _offers.Values.Select(o => new OfferDto
            {
                OfferId = o.OfferId,
                OwnerName = o.OwnerName,
                RootParents = o.RootParents ?? new(),
                Entries = o.Entries,
            }).ToList();
            File.WriteAllText(StorePath, JsonSerializer.Serialize(dtos));
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;
            var dtos = JsonSerializer.Deserialize<List<OfferDto>>(File.ReadAllText(StorePath));
            if (dtos == null)
                return;
            foreach (var d in dtos)
            {
                _offers[d.OfferId] = new FileOffer
                {
                    OfferId = d.OfferId,
                    OwnerName = d.OwnerName,
                    Entries = d.Entries,
                    RootParents = d.RootParents,
                };
            }
        }
        catch { }
    }
}
