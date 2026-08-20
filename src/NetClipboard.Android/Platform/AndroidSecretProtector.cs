using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using NetClipboard.Core.Security;

namespace NetClipboard.Droid.Platform;

/// <summary>
/// Il custode dei segreti su Android: il portachiavi di sistema.
///
/// E' la controparte di DPAPI su Windows, e la promessa e' la stessa: la chiave
/// privata del dispositivo e la chiave della cronologia restano leggibili solo a
/// questa applicazione su questo telefono. La differenza sta in dove vive la
/// chiave che avvolge le altre — qui non e' nemmeno un file: la genera il
/// sistema, resta nel portachiavi e (sui telefoni che ce l'hanno) dentro
/// l'elemento sicuro, e il codice non la vede mai. Si mandano byte a cifrare e
/// si ricevono byte cifrati.
///
/// Conseguenza voluta: cancellare i dati dell'applicazione, o ripristinare il
/// telefono, rende illeggibili sia l'identita' sia la cronologia. Non e' un
/// guasto — e' cio' che rende quei file inutili a chi se li portasse via.
///
/// Formato del blob: <c>[iv 12][ciphertext + tag]</c>, cioe' AES-256-GCM come
/// tutto il resto del progetto. L'IV lo sceglie il sistema a ogni cifratura, e va
/// riletto da li': imporne uno significherebbe, prima o poi, riusarlo.
/// </summary>
public sealed class AndroidSecretProtector : ISecretProtector
{
    private const string Alias = "netclipboard.secrets.v1";
    private const string ProviderName = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int IvBytes = 12;
    private const int TagBits = 128;

    private readonly IKey _key;

    public AndroidSecretProtector() => _key = LoadOrCreateKey();

    public byte[] Protect(byte[] plaintext)
    {
        var cipher = Cipher.GetInstance(Transformation)
                     ?? throw new InvalidOperationException("AES/GCM non disponibile");
        cipher.Init(CipherMode.EncryptMode, _key);

        var iv = cipher.GetIV() ?? throw new InvalidOperationException("il portachiavi non ha restituito un IV");
        var body = cipher.DoFinal(plaintext) ?? throw new InvalidOperationException("cifratura fallita");

        var blob = new byte[iv.Length + body.Length];
        Buffer.BlockCopy(iv, 0, blob, 0, iv.Length);
        Buffer.BlockCopy(body, 0, blob, iv.Length, body.Length);
        return blob;
    }

    public byte[]? Unprotect(byte[] wrapped)
    {
        if (wrapped.Length <= IvBytes) return null;
        try
        {
            var cipher = Cipher.GetInstance(Transformation);
            if (cipher == null) return null;

            var iv = new byte[IvBytes];
            Buffer.BlockCopy(wrapped, 0, iv, 0, IvBytes);
            cipher.Init(CipherMode.DecryptMode, _key, new GCMParameterSpec(TagBits, iv));

            return cipher.DoFinal(wrapped, IvBytes, wrapped.Length - IvBytes);
        }
        catch
        {
            // Chiave del portachiavi sparita (dati cancellati, telefono
            // ripristinato) o blob rovinato: non c'e' niente da recuperare, e
            // dirlo e' meglio che restituire byte a caso.
            return null;
        }
    }

    private static IKey LoadOrCreateKey()
    {
        var store = KeyStore.GetInstance(ProviderName)
                    ?? throw new InvalidOperationException("portachiavi di sistema non disponibile");
        store.Load(null);

        var existing = store.GetKey(Alias, null);
        if (existing != null) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, ProviderName)
                        ?? throw new InvalidOperationException("generatore AES non disponibile");

        var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            // Nessun vincolo di autenticazione dell'utente: il servizio deve poter
            // decifrare l'identita' all'avvio, anche a schermo bloccato, o non
            // risponderebbe piu' a nessuno finche' il telefono non viene sbloccato.
            .SetUserAuthenticationRequired(false)!
            .Build()!;

        generator.Init(spec);
        return generator.GenerateKey() ?? throw new InvalidOperationException("generazione della chiave fallita");
    }
}
