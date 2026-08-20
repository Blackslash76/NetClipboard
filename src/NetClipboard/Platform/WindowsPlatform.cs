using System.Security.Cryptography;
using NetClipboard.Core.Security;

namespace NetClipboard.Platform;

/// <summary>
/// Il custode dei segreti su Windows: DPAPI, ambito utente corrente. E' cio' che
/// <c>identity.key</c> e <c>history.key</c> hanno sempre usato — qui non cambia
/// niente rispetto a prima, cambia solo che il core non lo chiama piu' per nome.
/// </summary>
public sealed class WindowsSecretProtector : ISecretProtector
{
    public static readonly WindowsSecretProtector Instance = new();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

    public byte[]? Unprotect(byte[] wrapped)
    {
        try { return ProtectedData.Unprotect(wrapped, null, DataProtectionScope.CurrentUser); }
        catch { return null; } // profilo diverso, macchina diversa, file rovinato
    }
}

/// <summary>
/// L'analizzatore di Windows visto dal core: un involucro sottile intorno a
/// <see cref="AntimalwareScan"/>, che e' statico perche' il contesto AMSI e' uno
/// solo per processo e non ha senso averne due.
/// </summary>
public sealed class AmsiContentScanner : IContentScanner
{
    public static readonly AmsiContentScanner Instance = new();

    public bool Available => AntimalwareScan.Available;

    public ScanVerdict ScanBytes(byte[] data, string name) => AntimalwareScan.ScanBytes(data, name);
}
