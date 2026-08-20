namespace NetClipboard.Core;

public enum RateVerdict
{
    /// <summary>Ritmo normale: si procede.</summary>
    Ok,

    /// <summary>Attività sopra la norma: si procede ma si avvisa, una volta sola.</summary>
    Warn,

    /// <summary>Troppo: l'attività è sospesa fino a scadenza della penalità.</summary>
    Blocked,
}

/// <summary>
/// Freno all'attività a raffica, voluta o accidentale.
///
/// Il caso involontario è il più frequente: uno script che copia in ciclo, o una
/// macro impazzita, riempirebbero la rete e la cronologia di tutti. Quello
/// volontario è qualcuno che tempesta un collega di invii.
///
/// La progressione è deliberata: prima un avviso, che nella maggior parte dei
/// casi basta perché è l'utente stesso a fermarsi, e solo se l'attività continua
/// una sospensione a tempo. Bloccare subito punirebbe un picco legittimo — chi
/// copia tre cose di fila mentre lavora — che è cosa diversa da una raffica.
///
/// Conteggio a finestra scorrevole sui soli istanti recenti: niente timer, e la
/// memoria resta limitata dal numero di eventi che entrano nella finestra.
/// </summary>
public sealed class RateGuard
{
    private readonly int _warnCount;
    private readonly int _blockCount;
    private readonly long _windowMs;
    private readonly long _blockMs;

    private readonly Queue<long> _events = new();
    private readonly Lock _gate = new();

    private long _blockedUntil;
    private bool _warned;

    /// <param name="warnCount">Eventi nella finestra oltre i quali si avvisa.</param>
    /// <param name="blockCount">Eventi nella finestra oltre i quali si sospende.</param>
    /// <param name="window">Ampiezza della finestra di osservazione.</param>
    /// <param name="blockFor">Durata della sospensione.</param>
    public RateGuard(int warnCount, int blockCount, TimeSpan window, TimeSpan blockFor)
    {
        _warnCount = warnCount;
        _blockCount = blockCount;
        _windowMs = (long)window.TotalMilliseconds;
        _blockMs = (long)blockFor.TotalMilliseconds;
    }

    /// <summary>Secondi che mancano alla fine della sospensione (0 se non è attiva).</summary>
    public int BlockedSecondsLeft
    {
        get
        {
            lock (_gate)
            {
                var left = _blockedUntil - Environment.TickCount64;
                return left <= 0 ? 0 : (int)Math.Ceiling(left / 1000.0);
            }
        }
    }

    /// <summary>
    /// Registra un evento e dice se procedere. Va chiamato una volta per evento:
    /// è la chiamata stessa a contare.
    /// </summary>
    public RateVerdict Check()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;

            if (now < _blockedUntil) return RateVerdict.Blocked;

            // Uscendo dalla sospensione si riparte puliti, altrimenti gli eventi
            // vecchi la farebbero scattare di nuovo all'istante.
            if (_blockedUntil != 0)
            {
                _blockedUntil = 0;
                _events.Clear();
                _warned = false;
            }

            while (_events.Count > 0 && now - _events.Peek() > _windowMs) _events.Dequeue();
            _events.Enqueue(now);

            if (_events.Count > _blockCount)
            {
                _blockedUntil = now + _blockMs;
                _events.Clear();
                _warned = false;
                Log.Write($"[RateGuard] attività a raffica: sospensione per {_blockMs / 1000} s");
                return RateVerdict.Blocked;
            }

            if (_events.Count > _warnCount && !_warned)
            {
                _warned = true;
                Log.Write($"[RateGuard] attività sopra la norma: {_events.Count} eventi in {_windowMs / 1000} s");
                return RateVerdict.Warn;
            }

            return RateVerdict.Ok;
        }
    }
}
