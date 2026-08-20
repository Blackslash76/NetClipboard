namespace NetClipboard.Core.Security;

/// <summary>Esito di un controllo antimalware.</summary>
public enum ScanVerdict
{
    /// <summary>Nessun antivirus interpellabile, o contenuto troppo grande: nessun giudizio.</summary>
    NotScanned,

    /// <summary>Analizzato e ritenuto pulito.</summary>
    Clean,

    /// <summary>Riconosciuto come dannoso: non va consegnato.</summary>
    Malware,
}

/// <summary>
/// Chi, sulla piattaforma ospite, sa dare un giudizio su un contenuto in arrivo.
///
/// Su Windows e' AMSI, cioe' l'antivirus configurato sul PC. Su Android non
/// esiste un equivalente interpellabile da un'applicazione qualunque, e va
/// benissimo: l'assenza di analizzatore si dichiara con <see cref="Available"/>
/// falso e ogni esito diventa <see cref="ScanVerdict.NotScanned"/>.
///
/// La distinzione fra "analizzato e pulito" e "non analizzato" e' l'unica cosa
/// che conta qui: un bollino di verifica mostrato quando nessuno ha verificato
/// nulla e' peggio del bollino assente.
/// </summary>
public interface IContentScanner
{
    /// <summary>
    /// True solo se sulla macchina c'e' un motore che ha dimostrato di funzionare.
    /// Quando e' falso nessun contenuto viene mai dichiarato pulito.
    /// </summary>
    bool Available { get; }

    /// <summary>Giudizio su un buffer gia' in memoria. Il nome serve solo al motore come contesto.</summary>
    ScanVerdict ScanBytes(byte[] data, string name);
}

/// <summary>
/// L'analizzatore delle piattaforme che non ne hanno uno: non e' disponibile e
/// non giudica niente. Esplicito di proposito — un null sparso nel codice
/// costringerebbe ogni chiamante a ricordarsi cosa significa.
/// </summary>
public sealed class NoContentScanner : IContentScanner
{
    public static readonly NoContentScanner Instance = new();

    public bool Available => false;

    public ScanVerdict ScanBytes(byte[] data, string name) => ScanVerdict.NotScanned;
}
