using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NetClipboard.Core.Security;

/// <summary>
/// Cifratura dei file che l'applicazione tiene sul proprio disco.
///
/// <c>identity.key</c> era protetta da sempre; la cronologia no, e in
/// <c>%AppData%\NetClipboard\history</c> restavano in chiaro, per giorni, tutti i
/// testi e tutte le immagini passati dalla clipboard. Chiunque potesse leggere il
/// profilo — un backup, una cartella sincronizzata, un altro amministratore della
/// macchina — se li portava via senza toccare l'applicazione.
///
/// La chiave e' casuale, di 32 byte, tenuta in <c>history.key</c> e avvolta dal
/// custode del sistema (<see cref="ISecretProtector"/>): protegge quanto la
/// chiave di identita', cioe' contro chi legge i file ma non e' quell'utente su
/// quel dispositivo. Non protegge da codice eseguito dall'utente stesso, e non
/// pretende di farlo.
///
/// Non si fa passare ogni blob dal custode perche' un'immagine da qualche
/// megabyte finirebbe ogni volta nel servizio di protezione dei dati; AES-GCM
/// con la chiave gia' in mano costa una frazione, e il formato e' lo stesso gia'
/// usato sulla rete (<see cref="SessionCipher"/>).
/// </summary>
public sealed class LocalVault
{
    /// <summary>
    /// Firma in testa ai file cifrati. Serve a distinguerli da quelli scritti
    /// dalle versioni precedenti, che erano PNG o JSON nudi: senza, la migrazione
    /// dovrebbe indovinare.
    /// </summary>
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NCV1");

    private readonly SessionCipher _cipher;

    public LocalVault(string keyPath, ISecretProtector protector) =>
        _cipher = new SessionCipher(LoadOrCreateKey(keyPath, protector));

    /// <summary>True se i byte sono un blob di questo forziere (e non un file in chiaro di prima).</summary>
    public static bool IsSealed(byte[] data) =>
        data.Length >= Magic.Length && data.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    public byte[] Seal(byte[] plaintext)
    {
        var body = _cipher.Seal(plaintext);
        var blob = new byte[Magic.Length + body.Length];
        Buffer.BlockCopy(Magic, 0, blob, 0, Magic.Length);
        Buffer.BlockCopy(body, 0, blob, Magic.Length, body.Length);
        return blob;
    }

    /// <summary>
    /// Apre un blob. Un file <b>senza</b> la firma viene restituito com'e': e'
    /// roba scritta da una versione precedente, e si rilegge finche' non viene
    /// riscritta cifrata. Un file <b>con</b> la firma che non si decifra torna
    /// null e va scartato: significa che la chiave non e' piu' quella (profilo
    /// diverso, macchina diversa, file rovinato), e non c'e' modo di recuperarlo.
    /// </summary>
    public byte[]? Open(byte[] blob)
    {
        if (!IsSealed(blob)) return blob;
        return _cipher.Open(blob[Magic.Length..]);
    }

    // ----- Chiave -----

    private static byte[] LoadOrCreateKey(string path, ISecretProtector protector)
    {
        try
        {
            if (File.Exists(path))
            {
                var key = protector.Unprotect(File.ReadAllBytes(path));
                if (key is { Length: 32 }) return key;
            }
        }
        catch
        {
            // Chiave illeggibile: se ne fa una nuova. Cio' che era cifrato con la
            // vecchia diventa illeggibile e verra' scartato alla lettura — e' il
            // comportamento voluto: una cronologia che non si apre non e' un dato
            // da conservare a tempo indeterminato.
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        try { File.WriteAllBytes(path, protector.Protect(fresh)); }
        catch { } // se non si riesce a salvarla, la cronologia di questa sessione resta comunque cifrata
        return fresh;
    }
}
