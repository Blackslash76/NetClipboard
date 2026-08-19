// Prove di tenuta sui punti dove un errore costa caro: i parser che leggono dati
// dalla rete e il controllo di identita' del canale locale.
//
// Non serve una seconda macchina: si esercita il codice vero dell'applicazione con
// dati ostili costruiti qui. Da rilanciare dopo ogni modifica a ClipboardPayload,
// FileOffer o InstanceBridge.
//
//   dotnet run --project tools/selftest
using NetClipboard.Core;

var ok = 0; var ko = 0;
void Check(string what, bool passed, string detail = "")
{
    Console.WriteLine($"  [{(passed ? "ok " : "KO ")}] {what,-52} {detail}");
    if (passed) ok++; else ko++;
}

Console.WriteLine("== traffico normale: deve continuare a funzionare ==");

var text = ClipboardPayload.FromText(new string('x', 300_000));
var back = ClipboardPayload.Deserialize(text.Serialize());
Check("testo lungo 300.000 caratteri", back.Text == text.Text);

var png = new byte[512 * 1024];
Random.Shared.NextBytes(png);
var img = ClipboardPayload.Deserialize(ClipboardPayload.FromImage(png).Serialize());
Check("immagine da 512 KB", img.ImagePng!.SequenceEqual(png));

var offer = new FileOffer { OfferId = Guid.NewGuid(), OwnerDeviceId = "abc", OwnerName = "PC" };
for (var i = 0; i < 2_000; i++)
    offer.Entries.Add(new FileEntry { RootIndex = i, Size = i, RelativePath = $"cartella/file-{i}.txt" });
var got = ClipboardPayload.Deserialize(ClipboardPayload.FromOffer(offer).Serialize()).Offer!;
Check("offerta con 2.000 voci", got.Entries.Count == 2_000 && got.Entries[1999].RelativePath == "cartella/file-1999.txt");

Console.WriteLine();
Console.WriteLine("== dati ostili: devono essere rifiutati senza allocare ==");

static byte[] Hostile(int declared)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((byte)1); w.Write(declared); w.Write(new byte[4]);
    w.Flush(); return ms.ToArray();
}
static byte[] HostileOffer(int entries)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((byte)3); w.Write(Guid.NewGuid().ToByteArray()); w.Write(0); w.Write(0); w.Write(entries);
    w.Flush(); return ms.ToArray();
}

foreach (var declared in new[] { 600 * 1024 * 1024, int.MaxValue })
{
    var data = Hostile(declared);
    var before = GC.GetTotalAllocatedBytes();
    var rejected = false;
    try { ClipboardPayload.Deserialize(data); } catch (InvalidDataException) { rejected = true; }
    var mb = (GC.GetTotalAllocatedBytes() - before) / 1048576.0;
    Check($"9 byte che dichiarano {declared / 1048576} MB", rejected && mb < 1, $"allocati {mb:0.00} MB");
}

foreach (var n in new[] { 10_000_000, int.MaxValue })
{
    var data = HostileOffer(n);
    var rejected = false;
    try { ClipboardPayload.Deserialize(data); } catch (InvalidDataException) { rejected = true; }
    Check($"offerta che dichiara {n} voci", rejected);
}

Console.WriteLine();
Console.WriteLine("== identita' del chiamante sulla pipe ==");
// Non si usa la pipe vera: quella e' occupata dall'app in esecuzione, e una
// prova non deve andare a bussare al programma di chi lavora. Si ripete qui
// lo stesso meccanismo: il client concede l'impersonation, il servente legge
// il SID e lo confronta con il proprio.
{
    var name = "netclip-test-" + Guid.NewGuid().ToString("N");
    var verified = false; var refused = false;

    var server = Task.Run(() =>
    {
        using var srv = new System.IO.Pipes.NamedPipeServerStream(
            name, System.IO.Pipes.PipeDirection.In, 2);
        for (var round = 0; round < 2; round++)
        {
            srv.WaitForConnection();
            System.Security.Principal.SecurityIdentifier? sid = null;
            try
            {
                srv.RunAsClient(() => sid = System.Security.Principal.WindowsIdentity.GetCurrent().User);
                using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (sid != null && me.User != null && sid.Equals(me.User)) verified = true;
            }
            catch { refused = true; }
            srv.Disconnect();
        }
    });

    using (var c1 = new System.IO.Pipes.NamedPipeClientStream(".", name,
               System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.None,
               System.Security.Principal.TokenImpersonationLevel.Impersonation))
        c1.Connect(3000);

    using (var c2 = new System.IO.Pipes.NamedPipeClientStream(".", name,
               System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.None,
               System.Security.Principal.TokenImpersonationLevel.None))
        c2.Connect(3000);

    server.Wait(TimeSpan.FromSeconds(5));
    Check("client che concede l'identita': riconosciuto", verified);
    Check("client che non la concede: rifiutato", refused);
}

Console.WriteLine();
Console.WriteLine($"{ok} controlli superati, {ko} falliti.");

return ko == 0 ? 0 : 1;
