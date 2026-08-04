using System.Security.Cryptography;
using System.Text;

namespace NetClipboard.Core;

/// <summary>
/// Cifratura simmetrica AES-256-GCM con chiave derivata dalla passphrase
/// condivisa (PBKDF2). Ogni messaggio ha un nonce casuale.
///
/// Formato blob: [nonce 12][tag 16][ciphertext N].
///
/// Lo stesso canale protegge sia gli annunci UDP che i trasferimenti TCP:
/// un peer che non conosce la password non riesce a decifrare, quindi non
/// viene nemmeno "scoperto".
/// </summary>
public sealed class SecureChannel
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256 bit

    // Salt fisso dell'applicazione: la sicurezza sta nella passphrase.
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("NetClipboard::v1::pbkdf2::salt");

    private byte[]? _key;

    public bool HasKey => _key != null;

    public void SetPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            _key = null;
            return;
        }
        _key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Salt,
            iterations: 120_000,
            HashAlgorithmName.SHA256,
            KeySize);
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var key = _key ?? throw new InvalidOperationException("Nessuna password impostata.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, blob, NonceSize + TagSize, ciphertext.Length);
        return blob;
    }

    /// <summary>Decifra; ritorna null se la chiave e' assente o i dati non sono validi.</summary>
    public byte[]? TryDecrypt(byte[] blob)
    {
        var key = _key;
        if (key == null || blob.Length < NonceSize + TagSize)
            return null;

        try
        {
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipherLen = blob.Length - NonceSize - TagSize;
            var ciphertext = new byte[cipherLen];

            Buffer.BlockCopy(blob, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(blob, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(blob, NonceSize + TagSize, ciphertext, 0, cipherLen);

            var plaintext = new byte[cipherLen];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null; // password sbagliata o dati manomessi
        }
        catch
        {
            return null;
        }
    }
}
