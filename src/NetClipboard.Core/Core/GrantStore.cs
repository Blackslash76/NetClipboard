using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace NetClipboard.Core;

/// <summary>
/// Permessi di prelievo file, con la loro scadenza, tenuti anche su disco.
///
/// Prima vivevano solo in memoria: se una delle due applicazioni si riavviava fra
/// l'accettazione e l'incolla, i file erano persi. Il messaggio era onesto ("il
/// permesso non e' piu' valido") ma la funzione si perdeva per un motivo che con
/// il permesso non c'entrava nulla.
///
/// Se ne usano due istanze, una per verso: quelli che <b>concediamo</b> (il
/// dispositivo X puo' scaricare la nostra offerta Y) e quelli che <b>abbiamo
/// ottenuto</b> accettando un invio. Sono file separati perche' sono elenchi
/// diversi, e mescolarli renderebbe possibile scambiare un permesso ricevuto per
/// uno concesso.
///
/// La scadenza e' un istante assoluto UTC e non un <c>Environment.TickCount64</c>:
/// il contatore riparte da zero a ogni riavvio, quindi un permesso salvato con
/// quello tornerebbe valido — o scaduto — a caso.
/// </summary>
public sealed class GrantStore
{
    private readonly string _path;
    private readonly TimeSpan _lifetime;
    private readonly ConcurrentDictionary<string, DateTime> _expiry = new();
    private readonly Lock _gate = new();

    public GrantStore(string path, TimeSpan lifetime)
    {
        _path = path;
        _lifetime = lifetime;
        Load();
    }

    private static string Key(string deviceId, Guid offerId) => deviceId + ":" + offerId.ToString("N");

    /// <summary>Concede (o rinnova) il permesso, facendolo partire da adesso.</summary>
    public void Grant(string deviceId, Guid offerId)
    {
        _expiry[Key(deviceId, offerId)] = DateTime.UtcNow + _lifetime;
        Persist();
    }

    /// <summary>
    /// Permesso valido solo dentro la finestra. La scadenza si verifica in lettura
    /// e la voce si toglie li' per li': niente timer di pulizia, e un permesso
    /// scaduto non puo' tornare buono per una svista.
    /// </summary>
    public bool IsValid(string deviceId, Guid offerId)
    {
        var key = Key(deviceId, offerId);
        if (!_expiry.TryGetValue(key, out var until)) return false;
        if (DateTime.UtcNow <= until) return true;
        if (_expiry.TryRemove(key, out _)) Persist();
        return false;
    }

    /// <summary>Toglie il permesso: l'invio e' stato rifiutato o non e' andato a buon fine.</summary>
    public void Revoke(string deviceId, Guid offerId)
    {
        if (_expiry.TryRemove(Key(deviceId, offerId), out _)) Persist();
    }

    // ----- Persistenza -----

    private void Persist()
    {
        lock (_gate)
        {
            try
            {
                // Si scrive solo cio' che e' ancora vivo: il file non cresce e un
                // permesso scaduto non sopravvive al riavvio nemmeno per un istante.
                var now = DateTime.UtcNow;
                foreach (var (k, until) in _expiry)
                    if (until < now) _expiry.TryRemove(k, out _);
                File.WriteAllText(_path, JsonSerializer.Serialize(
                    _expiry.ToDictionary(kv => kv.Key, kv => kv.Value)));
            }
            catch { }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path));
            if (map == null) return;
            var now = DateTime.UtcNow;
            foreach (var (k, until) in map)
                if (until > now) _expiry[k] = until;
        }
        catch { }
    }
}
