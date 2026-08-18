using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using NetClipboard.Core;

namespace NetClipboard.Net;

/// <summary>
/// Canale fra il processo che Windows lancia dal menu "Invia a" e l'istanza già
/// in esecuzione nella tray.
///
/// Serve perché l'app è a istanza singola: quando Explorer avvia l'eseguibile
/// con dei file selezionati, quel processo non può aprire una finestra propria —
/// non ha né la rete né i dispositivi. Consegna i percorsi a chi sta già girando
/// e termina subito.
/// </summary>
public static class InstanceBridge
{
    private const string PipeName = "NetClipboard.SendTo.6f0c2b1e";
    private const int MaxPaths = 512;
    private const int MaxPathLength = 4096;

    /// <summary>
    /// Consegna i percorsi all'istanza in esecuzione. False se non c'è nessuno in
    /// ascolto, cioè se NetClipboard non è avviato.
    /// </summary>
    public static bool TrySend(IReadOnlyList<string> paths, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);

            using var w = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true);
            var send = paths.Take(MaxPaths).ToList();
            w.Write(send.Count);
            foreach (var p in send) w.Write(p);
            w.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resta in ascolto e passa i percorsi ricevuti. Una connessione per volta:
    /// il traffico è un clic ogni tanto, non un flusso.
    /// </summary>
    public static void Listen(Action<IReadOnlyList<string>> onPaths, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);
                    if (!SameUser(server)) continue;

                    var paths = Read(server);
                    if (paths.Count > 0) onPaths(paths);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Write($"[SendTo] richiesta non gestita: {ex.Message}");
                }
            }
        }, ct);
    }

    /// <summary>
    /// La pipe ha un nome noto a tutta la macchina: si accettano richieste solo
    /// dallo stesso utente, altrimenti un altro account potrebbe far comparire
    /// finestre sulla sessione altrui.
    /// </summary>
    private static bool SameUser(NamedPipeServerStream server)
    {
        try
        {
            var client = server.GetImpersonationUserName();
            var me = WindowsIdentity.GetCurrent().Name;

            // GetImpersonationUserName può restituire il solo nome utente, senza dominio.
            var mine = me.Contains('\\') ? me[(me.IndexOf('\\') + 1)..] : me;
            var theirs = client.Contains('\\') ? client[(client.IndexOf('\\') + 1)..] : client;

            if (string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase)) return true;
            Log.Write($"[SendTo] richiesta rifiutata: arriva da '{client}', non da '{me}'.");
            return false;
        }
        catch
        {
            return true; // se l'identità non è verificabile non si blocca l'uso normale
        }
    }

    private static List<string> Read(Stream s)
    {
        var paths = new List<string>();
        try
        {
            using var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
            var count = r.ReadInt32();
            if (count <= 0 || count > MaxPaths) return paths;

            for (var i = 0; i < count; i++)
            {
                var p = r.ReadString();
                if (p.Length is > 0 and <= MaxPathLength) paths.Add(p);
            }
        }
        catch (EndOfStreamException) { }
        return paths;
    }
}
