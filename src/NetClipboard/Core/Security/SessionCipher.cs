using System.Security.Cryptography;

namespace NetClipboard.Core.Security;

/// <summary>
/// Cifrario AES-256-GCM per una singola sessione autenticata, con la chiave
/// derivata dall'handshake. Formato blob: [nonce 12][tag 16][ciphertext N].
/// </summary>
public sealed class SessionCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SessionCipher(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("La chiave di sessione deve essere di 32 byte.", nameof(key));
        _key = key;
    }

    public byte[] Seal(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, blob, NonceSize + TagSize, ciphertext.Length);
        return blob;
    }

    public byte[]? Open(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize)
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
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch
        {
            return null;
        }
    }
}
