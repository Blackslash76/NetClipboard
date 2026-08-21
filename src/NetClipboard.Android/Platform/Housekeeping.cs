using NetClipboard.Core;

namespace NetClipboard.Droid.Platform;

/// <summary>
/// Le pulizie dello spazio, in un posto solo e con una guardia di tempo.
///
/// Ognuna delle tre cartelle che crescono aveva gia' la sua regola — i file
/// ricevuti scadono, le copie in uscita durano un giorno, i file prestati agli
/// appunti pure — ma le regole scattavano nei posti sbagliati: quella delle copie
/// in uscita solo <b>mentre si condivide qualcosa di nuovo</b>, le altre due solo
/// all'avvio del servizio. Chi condivide un video da mezzo giga e poi smette di
/// condividere non fa scattare piu' niente, e il servizio in primo piano puo'
/// stare su per settimane senza riavviarsi: la politica c'era, e non la eseguiva
/// nessuno.
///
/// Qui stanno insieme e si chiamano nei momenti in cui lo spazio cresce davvero:
/// l'avvio e gli arrivi. La guardia impedisce che diventi un lavoro a ogni copia.
/// </summary>
internal static class Housekeeping
{
    /// <summary>Ogni quanto, al massimo, ci si prende la briga di guardare.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private static readonly Lock Gate = new();
    private static DateTime _lastUtc = DateTime.MinValue;

    /// <param name="force">Salta la guardia di tempo. Vero all'avvio del servizio.</param>
    public static void Run(AppConfig config, bool force = false)
    {
        lock (Gate)
        {
            if (!force && DateTime.UtcNow - _lastUtc < Every) return;
            _lastUtc = DateTime.UtcNow;
        }

        var context = Android.App.Application.Context;

        // Tre pulizie indipendenti: se una non riesce, le altre devono farsi
        // comunque. Un permesso negato su una cartella non deve lasciare piene
        // le altre due.
        Try("file ricevuti", () => ClipboardHistory.CleanupReceived(config.HistoryMaxAgeDays));
        Try("copie in uscita", () => OutgoingStore.Prune(context));
        Try("appoggio appunti", () => IncomingStore.PruneStaged(context));
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"[Pulizia] {what}: {ex.Message}"); }
    }
}
