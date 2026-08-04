using System.Net;

namespace NetClipboard.Net;

/// <summary>Un altro PC scoperto sulla rete tramite gli annunci UDP.</summary>
public sealed class Peer
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required IPAddress Address { get; set; }
    public required int TcpPort { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public IPEndPoint EndPoint => new(Address, TcpPort);

    public override string ToString() => $"{Name} ({Address})";
}
