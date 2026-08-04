using System.Net;

namespace NetClipboard.Net;

/// <summary>Un dispositivo scoperto sulla rete, identificato dalla sua chiave (DeviceId).</summary>
public sealed class Peer
{
    public required string DeviceId { get; init; }
    public required string Name { get; set; }
    public required IPAddress Address { get; set; }
    public required int Port { get; set; }

    /// <summary>Chiave pubblica (SPKI DER) verificata durante l'handshake.</summary>
    public byte[]? PublicKeyDer { get; set; }

    /// <summary>True se il dispositivo è nella lista dei fidati (pairing avvenuto).</summary>
    public bool Trusted { get; set; }

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public IPEndPoint EndPoint => new(Address, Port);

    public override string ToString() => $"{Name} ({Address})";
}
