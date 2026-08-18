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

    /// <summary>
    /// Identità aziendale dichiarata dal peer nel ping, se ne ha una.
    ///
    /// ATTENZIONE: al momento è un dato DICHIARATO, non verificato: viaggia come
    /// campo in chiaro e chiunque potrebbe scriverci un nome altrui. Serve solo a
    /// rendere leggibile l'elenco "Invia a…". La sicurezza sta nella conferma
    /// esplicita di chi riceve. La verifica vera (firma dell'ID token contro le
    /// chiavi pubbliche del tenant) arriva quando ci sarà l'app registration.
    /// </summary>
    public Core.Identity.WorkIdentity? Work { get; set; }

    /// <summary>Come chiamare questo peer nell'interfaccia: la persona se c'è, altrimenti la macchina.</summary>
    public string Label => Work != null ? $"{Work.Label} · {Name}" : Name;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public IPEndPoint EndPoint => new(Address, Port);

    public override string ToString() => $"{Name} ({Address})";
}
