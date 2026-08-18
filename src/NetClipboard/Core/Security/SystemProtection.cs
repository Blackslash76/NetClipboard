using System.Runtime.InteropServices;

namespace NetClipboard.Core.Security;

public enum ProtectionState
{
    /// <summary>Windows non sa dirlo (Centro sicurezza assente, es. edizioni Server).</summary>
    Unknown,

    /// <summary>C'è un antivirus che Windows considera attivo e aggiornato.</summary>
    Active,

    /// <summary>Nessuna protezione, o protezione segnalata come non in salute.</summary>
    Inactive,
}

/// <summary>
/// Stato della protezione antivirus secondo il Centro sicurezza di Windows.
///
/// Serve a dire una verità più debole ma vera, quando quella forte non è
/// ottenibile. <see cref="AntimalwareScan"/> dà un verdetto per singolo
/// contenuto, ma solo se il motore installato risponde ad AMSI: parecchi
/// prodotti gestiti centralmente non lo fanno, pur proteggendo eccome il PC.
/// In quei casi non si può dire "questo file è pulito", ma si può dire
/// "l'antivirus è attivo e i file vengono controllati mentre arrivano" — che è
/// esattamente ciò che sta succedendo, e a chi riceve serve saperlo.
///
/// Si usa wscapi.dll invece di WMI: una chiamata sola, nessun pacchetto in più
/// da imbarcare nell'eseguibile.
/// </summary>
public static class SystemProtection
{
    private const int ProviderAntivirus = 4;

    // Salute riportata dal Centro sicurezza.
    private const int HealthGood = 0;
    private const int HealthNotMonitored = 1;
    private const int HealthPoor = 2;
    private const int HealthSnooze = 3;

    private static readonly Lock Gate = new();
    private static ProtectionState? _cached;

    /// <summary>
    /// Stato della protezione. Interrogato una volta sola: cambia di rado, e
    /// mostrarlo in una finestra non giustifica una chiamata di sistema ogni volta.
    /// </summary>
    public static ProtectionState Antivirus
    {
        get
        {
            lock (Gate)
            {
                _cached ??= Query();
                return _cached.Value;
            }
        }
    }

    private static ProtectionState Query()
    {
        try
        {
            var hr = WscGetSecurityProviderHealth(ProviderAntivirus, out var health);
            if (hr != 0)
            {
                Log.Write($"[Protezione] Centro sicurezza non interrogabile (0x{hr:X8}).");
                return ProtectionState.Unknown;
            }

            var state = health switch
            {
                HealthGood => ProtectionState.Active,
                HealthSnooze or HealthPoor => ProtectionState.Inactive,
                HealthNotMonitored => ProtectionState.Unknown,
                _ => ProtectionState.Unknown,
            };
            Log.Write($"[Protezione] antivirus di sistema: {state} (salute {health}).");
            return state;
        }
        catch (Exception ex)
        {
            Log.Write($"[Protezione] stato non determinabile: {ex.Message}");
            return ProtectionState.Unknown;
        }
    }

    [DllImport("wscapi.dll")]
    private static extern int WscGetSecurityProviderHealth(int providers, out int health);
}
