using System.Text.Json;

namespace NetClipboard.Core.Identity;

/// <summary>
/// Identità aziendale (Microsoft Entra ID) di chi sta usando il PC, estratta
/// dalle claim dell'ID token.
///
/// Non sostituisce <see cref="Security.DeviceIdentity"/>: quella resta la
/// credenziale crittografica del dispositivo. Questa è un attributo verificato
/// che dice CHI sta davanti alla tastiera e in QUALE organizzazione, e serve a
/// due cose: limitare il pairing al proprio tenant e mostrare nomi veri al
/// posto dei nomi macchina.
/// </summary>
public sealed record WorkIdentity(
    string TenantId,
    string ObjectId,
    string UserPrincipalName,
    string DisplayName)
{
    /// <summary>Identificatore stabile dell'utente: unico a livello globale, non riassegnabile.</summary>
    public string Key => $"{TenantId}/{ObjectId}";

    /// <summary>Etichetta da mostrare: il nome vero se c'è, altrimenti l'indirizzo.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(DisplayName) ? UserPrincipalName : DisplayName;

    /// <summary>True se le due identità sono della stessa organizzazione.</summary>
    public bool SameTenantAs(WorkIdentity other) =>
        !string.IsNullOrEmpty(TenantId) &&
        string.Equals(TenantId, other.TenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Legge le claim dal payload di un ID token (JWT).
    ///
    /// ATTENZIONE: qui NON si verifica la firma. Va bene per il PROPRIO token,
    /// che arriva da MSAL su canale sicuro; per il token di un peer serve la
    /// validazione contro le chiavi pubbliche del tenant, oltre al confronto
    /// visivo del codice SAS che già autentica il canale di pairing.
    /// </summary>
    public static WorkIdentity? FromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            using var doc = JsonDocument.Parse(DecodeSegment(parts[1]));
            var root = doc.RootElement;

            var tenant = Claim(root, "tid");
            var oid = Claim(root, "oid");
            if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(oid))
                return null; // senza tenant+utente non è un'identità aziendale utilizzabile

            var upn = Claim(root, "preferred_username");
            if (string.IsNullOrEmpty(upn)) upn = Claim(root, "upn");
            if (string.IsNullOrEmpty(upn)) upn = Claim(root, "email");

            return new WorkIdentity(tenant, oid, upn, Claim(root, "name"));
        }
        catch
        {
            return null; // token malformato: si prosegue senza identità aziendale
        }
    }

    private static string Claim(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Base64url → byte. I JWT omettono il padding '='.</summary>
    private static byte[] DecodeSegment(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public override string ToString() => $"{Label} <{UserPrincipalName}> @{TenantId}";
}
