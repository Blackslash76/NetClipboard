using System.IO;
using System.Security.Cryptography;

namespace NetClipboard.Core.Security;

/// <summary>
/// Identità crittografica del dispositivo: una coppia di chiavi ECDSA P-256.
/// La chiave privata non lascia mai il dispositivo (la custodisce il sistema,
/// tramite <see cref="ISecretProtector"/>). L'ID dispositivo è l'impronta
/// (SHA-256) della chiave pubblica: non falsificabile.
///
/// Usa solo API del framework .NET, nessuna dipendenza esterna: e' lo stesso
/// codice su Windows e su Android, e le due parti si riconoscono proprio perche'
/// firmano e verificano con lo stesso identico algoritmo.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    /// <summary>Chiave pubblica in formato SubjectPublicKeyInfo (DER).</summary>
    public byte[] PublicKeyDer { get; }

    /// <summary>ID canonico: SHA-256 della chiave pubblica, esadecimale (64 char).</summary>
    public string DeviceId { get; }

    private DeviceIdentity(ECDsa key)
    {
        _key = key;
        PublicKeyDer = key.ExportSubjectPublicKeyInfo();
        DeviceId = IdFromPublicKey(PublicKeyDer);
    }

    public static DeviceIdentity CreateEphemeral() =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// Identita' da una chiave privata gia' in mano (PKCS#8). Serve al banco di
    /// conformita', che con chiavi fissate ottiene impronte e firme riproducibili,
    /// e alle piattaforme che custodiscono la chiave a modo loro invece che in un
    /// file avvolto da <see cref="ISecretProtector"/>.
    /// </summary>
    public static DeviceIdentity FromPkcs8(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new DeviceIdentity(key);
    }

    /// <summary>
    /// Carica l'identità dal disco o la crea al primo avvio, affidando la chiave
    /// privata al custode del sistema ospite.
    /// </summary>
    /// <param name="protector">Chi avvolge la chiave privata: DPAPI su Windows, il portachiavi su Android.</param>
    /// <param name="path">Dove sta il file; per default <c>identity.key</c> nella cartella dell'applicazione.</param>
    public static DeviceIdentity LoadOrCreate(ISecretProtector protector, string? path = null)
    {
        path ??= Path.Combine(AppConfig.AppDataDir, "identity.key");
        try
        {
            if (File.Exists(path))
            {
                var pkcs8 = protector.Unprotect(File.ReadAllBytes(path));
                if (pkcs8 != null)
                {
                    var key = ECDsa.Create();
                    key.ImportPkcs8PrivateKey(pkcs8, out _);
                    return new DeviceIdentity(key);
                }
            }
        }
        catch
        {
            // chiave corrotta o non leggibile: se ne genera una nuova
        }

        var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            File.WriteAllBytes(path, protector.Protect(created.ExportPkcs8PrivateKey()));
        }
        catch
        {
            // best effort: se non si salva, l'identità vive solo per questa sessione
        }
        return new DeviceIdentity(created);
    }

    /// <summary>Firma dati con la chiave identità (ECDSA/SHA-256).</summary>
    public byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256);

    public static bool Verify(byte[] publicKeyDer, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public static string IdFromPublicKey(byte[] publicKeyDer) =>
        Convert.ToHexString(SHA256.HashData(publicKeyDer));

    /// <summary>Impronta leggibile (primi 8 byte in gruppi), per confronto visivo.</summary>
    public static string ShortFingerprint(string deviceId)
    {
        var head = deviceId.Length >= 16 ? deviceId[..16] : deviceId;
        var groups = new List<string>();
        for (var i = 0; i < head.Length; i += 4)
            groups.Add(head.Substring(i, Math.Min(4, head.Length - i)));
        return string.Join("-", groups);
    }

    public void Dispose() => _key.Dispose();
}
