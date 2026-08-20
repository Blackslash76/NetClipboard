namespace NetClipboard.Droid.Platform;

/// <summary>Una domanda da porre a chi usa il telefono, e le due risposte possibili.</summary>
public sealed record PromptRequest(string Title, string Body, string Accept, string Reject);

/// <summary>
/// Il ponte fra il trasporto, che chiede una conferma e <b>aspetta</b> su un
/// thread suo, e l'interfaccia, che una domanda la puo' mostrare solo sul thread
/// dell'interfaccia e risponde quando le pare.
///
/// Su Windows sono finestre modali e la cosa si risolve da se'. Qui no, e non e'
/// un dettaglio implementativo: se nessuno puo' rispondere — l'applicazione e'
/// chiusa e sta girando solo il servizio — la risposta giusta e' "nessuna
/// risposta", mai "sì". Un accoppiamento confermato da nessuno non e' un
/// accoppiamento.
/// </summary>
public static class Prompts
{
    /// <summary>
    /// Chi mostra le domande. Lo registra l'interfaccia quando compare, e lo
    /// toglie quando sparisce. Il token serve a farla sparire da sola quando la
    /// risposta non interessa piu' (l'altro dispositivo ha annullato).
    /// </summary>
    public static Func<PromptRequest, CancellationToken, Task<bool?>>? Handler;

    /// <summary>
    /// Pone la domanda e aspetta. Restituisce null se non c'e' nessuno a cui
    /// chiedere o se il tempo e' scaduto: per il pairing e gli invii vale come
    /// no, per le presentazioni vale come "riproponila", che e' quanto il core si
    /// aspetta in quel caso.
    /// </summary>
    /// <remarks>
    /// Da chiamare solo da un thread che NON sia quello dell'interfaccia: qui si
    /// aspetta, e chi mostra la domanda ha bisogno proprio di quel thread per
    /// mostrarla. Chi avvia un'operazione dall'interfaccia deve passare da un
    /// <c>Task.Run</c> (vedi <c>NetClipboardHost.PairAsync</c>).
    /// </remarks>
    public static bool? Ask(PromptRequest request, TimeSpan timeout, CancellationToken giveUp = default)
    {
        var handler = Handler;
        if (handler == null || giveUp.IsCancellationRequested) return null;
        try
        {
            return handler(request, giveUp).WaitAsync(timeout, giveUp).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null; // l'altro dispositivo ha annullato: non e' un no nostro
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch
        {
            return null; // interfaccia sparita mentre la domanda era aperta
        }
    }
}
