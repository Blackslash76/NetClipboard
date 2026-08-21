namespace NetClipboard.Core;

/// <summary>
/// L'ora di un contenuto detta come la direbbe una persona: "adesso", "5 min
/// fa", e la data quando ormai e' di ieri.
///
/// Sta nel core e non nell'interfaccia perche' la stessa riga di cronologia si
/// legge sul PC e sul telefono: due implementazioni separate finirebbero per
/// mostrare due orari diversi per la stessa voce, e la prima volta che accade
/// nessuno pensa a un arrotondamento — si pensa che i dispositivi siano
/// disallineati.
/// </summary>
public static class TimeText
{
    /// <summary>Momento passato, raccontato in forma relativa. Prende un UTC, risponde nell'ora locale.</summary>
    public static string Relative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTime.Now - local;
        if (delta.TotalSeconds < 60) return L.T("time.now");
        if (delta.TotalMinutes < 60) return L.T("time.minutesAgo", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return L.T("time.hoursAgo", (int)delta.TotalHours);
        return local.ToString(L.T("time.dateFormat"));
    }
}
