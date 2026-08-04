using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NetClipboard.Core.Security;

public sealed record HandshakeResult(
    byte[] SessionKey,
    string Sas,
    byte[] PeerPublicKeyDer,
    string PeerDeviceId,
    byte[] Transcript);

/// <summary>
/// Handshake autenticato in stile Station-to-Station:
///  - ECDH effimero P-256 per la forward secrecy (una chiave di sessione nuova ogni volta);
///  - ciascun lato firma il "transcript" con la propria chiave identità → autenticazione;
///  - dal segreto condiviso si deriva anche un codice SAS a 6 cifre: se non c'è un
///    intercettatore attivo, i due PC mostrano lo STESSO codice (numeric comparison).
///
/// Sequenza (Init = A, Resp = B):
///   A → B : [idPubA][ephPubA]
///   B → A : [idPubB][ephPubB][sigB(transcript)]
///   A → B : [sigA(transcript)]
/// transcript = SHA256( idPubInit | idPubResp | ephPubInit | ephPubResp )
/// </summary>
public sealed class Handshaker : IDisposable
{
    private static readonly byte[] SessionInfo = Encoding.ASCII.GetBytes("netclip-session-v1");
    private static readonly byte[] SasInfo = Encoding.ASCII.GetBytes("netclip-sas-v1");

    private readonly DeviceIdentity _self;
    private readonly ECDiffieHellman _eph;

    public byte[] IdPublicKey => _self.PublicKeyDer;
    public byte[] EphPublicKey { get; }

    public Handshaker(DeviceIdentity self)
    {
        _self = self;
        _eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        EphPublicKey = _eph.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Deriva chiave di sessione e SAS dai materiali del peer. L'ordinamento
    /// Init/Resp deve essere coerente sui due lati (stesso transcript).
    /// </summary>
    public HandshakeResult Complete(byte[] peerIdPub, byte[] peerEphPub, bool selfIsInitiator)
    {
        var idInit = selfIsInitiator ? _self.PublicKeyDer : peerIdPub;
        var idResp = selfIsInitiator ? peerIdPub : _self.PublicKeyDer;
        var ephInit = selfIsInitiator ? EphPublicKey : peerEphPub;
        var ephResp = selfIsInitiator ? peerEphPub : EphPublicKey;
        var transcript = ComputeTranscript(idInit, idResp, ephInit, ephResp);

        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerEphPub, out _);
        var shared = _eph.DeriveKeyFromHash(peerEcdh.PublicKey, HashAlgorithmName.SHA256);

        var sessionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, transcript, SessionInfo);
        var sasBytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 4, transcript, SasInfo);
        var sas = (BitConverter.ToUInt32(sasBytes, 0) % 1_000_000).ToString("D6");

        var peerId = DeviceIdentity.IdFromPublicKey(peerIdPub);
        return new HandshakeResult(sessionKey, sas, peerIdPub, peerId, transcript);
    }

    public byte[] SignTranscript(byte[] transcript) => _self.Sign(transcript);

    public static bool VerifyPeer(byte[] peerIdPub, byte[] transcript, byte[] signature) =>
        DeviceIdentity.Verify(peerIdPub, transcript, signature);

    private static byte[] ComputeTranscript(byte[] idInit, byte[] idResp, byte[] ephInit, byte[] ephResp)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var part in new[] { idInit, idResp, ephInit, ephResp })
            {
                w.Write(part.Length);
                w.Write(part);
            }
        }
        return SHA256.HashData(ms.ToArray());
    }

    public void Dispose() => _eph.Dispose();
}
