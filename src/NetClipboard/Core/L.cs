using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace NetClipboard.Core;

/// <summary>
/// Catalogo dei testi mostrati all'utente.
///
/// Nessuna stringa dell'interfaccia va scritta nel codice: si recupera per chiave
/// con <see cref="T(string)"/>. Le lingue stanno in <c>Resources/xx.json</c>,
/// incorporati nell'eseguibile: niente assembly satellite, che complicherebbero
/// la distribuzione single-file self-contained.
///
/// Aggiungere una lingua = aggiungere un file <c>Resources/en.json</c> con le
/// stesse chiavi (il csproj li include tutti automaticamente).
/// </summary>
public static class L
{
    private const string Prefix = "NetClipboard.Resources.";
    private const string Suffix = ".json";

    /// <summary>Lingua sempre presente, usata come rete di sicurezza per le chiavi mancanti.</summary>
    public const string FallbackLanguage = "it";

    private static readonly Dictionary<string, string> Fallback;
    private static Dictionary<string, string> _current;

    static L()
    {
        Fallback = Load(FallbackLanguage) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _current = Fallback;
    }

    /// <summary>Lingua attualmente in uso (codice a due lettere).</summary>
    public static string Language { get; private set; } = FallbackLanguage;

    /// <summary>Lingue disponibili fra le risorse incorporate.</summary>
    public static IReadOnlyList<string> Available { get; } = Discover();

    /// <summary>
    /// Sceglie la lingua: quella richiesta se esiste, altrimenti quella di Windows,
    /// altrimenti il fallback. Da chiamare una volta all'avvio, prima della UI.
    /// </summary>
    public static void Init(string? preferred = null)
    {
        foreach (var candidate in new[] { preferred, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var code = candidate.Trim().ToLowerInvariant();
            if (code == Language) return;
            var loaded = Load(code);
            if (loaded == null) continue;
            _current = loaded;
            Language = code;
            return;
        }
    }

    /// <summary>Testo per la chiave indicata. Se manca ovunque restituisce la chiave stessa (così il buco si vede).</summary>
    public static string T(string key)
    {
        if (_current.TryGetValue(key, out var s)) return s;
        return Fallback.TryGetValue(key, out var f) ? f : key;
    }

    /// <summary>Testo con segnaposto, es. <c>T("pairing.done", nome)</c> su "Accoppiato con {0}".</summary>
    public static string T(string key, params object?[] args)
    {
        var format = T(key);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; } // segnaposto sbagliati: meglio il testo grezzo di un crash
    }

    private static IReadOnlyList<string> Discover()
    {
        try
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
                .Select(n => n[Prefix.Length..^Suffix.Length].ToLowerInvariant())
                .OrderBy(n => n)
                .ToList();
        }
        catch { return new List<string> { FallbackLanguage }; }
    }

    private static Dictionary<string, string>? Load(string language)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(Prefix + language + Suffix);
            if (stream == null) return null;

            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, options);
            return map == null ? null : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch
        {
            return null; // catalogo illeggibile: si continua col fallback
        }
    }
}
