using NetClipboard.Core;

namespace NetClipboard.E2E;

/// <summary>
/// Prove che hanno bisogno della clipboard vera di Windows: i marcatori con cui i
/// gestori di password dicono "questo non va propagato", e la lettura dei formati
/// ricchi.
///
/// Non girano insieme al resto e non stanno in CI: la clipboard e' una sola per
/// sessione, e una prova che se ne appropria disturberebbe chi sta lavorando. Si
/// chiedono a mano con <c>--clipboard</c>, e alla fine si rimette dentro cio' che
/// c'era. Sui runner senza sessione interattiva non ci sarebbe nemmeno una
/// clipboard da usare.
/// </summary>
public static class ClipboardChecks
{
    public static int Run()
    {
        var ok = 0; var ko = 0;
        void Check(string what, bool passed, string detail = "")
        {
            Console.WriteLine($"  [{(passed ? "ok " : "KO ")}] {what,-56} {detail}");
            if (passed) ok++; else ko++;
        }

        // La clipboard vuole un thread STA, e questo programma non ne ha uno.
        var t = new Thread(() =>
        {
            var monitor = new ClipboardMonitor(new AppConfig());

            var saved = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            try
            {
                Console.WriteLine("== marcatori dei gestori di password ==");

                Set(o => o.SetData(DataFormats.UnicodeText, "una copia qualunque"));
                Check("copia normale: passa", !ClipboardMonitor.IsSecretClipboard()
                    && monitor.TryReadClipboard()?.Text == "una copia qualunque");

                foreach (var flag in new[] { "Clipboard Viewer Ignore", "ExcludeClipboardContentFromMonitorProcessing" })
                {
                    var name = flag;
                    Set(o =>
                    {
                        o.SetData(DataFormats.UnicodeText, "hunter2");
                        o.SetData(name, new MemoryStream(new byte[] { 0 }));
                    });
                    Check($"\"{name}\": non si legge",
                        ClipboardMonitor.IsSecretClipboard() && monitor.TryReadClipboard() == null);
                }

                foreach (var (name, value, secret) in new[]
                         {
                             ("CanIncludeInClipboardHistory", 0, true),
                             ("CanIncludeInClipboardHistory", 1, false),
                             ("CanUploadToCloudClipboard", 0, true),
                         })
                {
                    var fmt = name; var v = value;
                    Set(o =>
                    {
                        o.SetData(DataFormats.UnicodeText, "hunter2");
                        o.SetData(fmt, new MemoryStream(BitConverter.GetBytes(v)));
                    });
                    Check($"{name} = {value}: {(secret ? "riservato" : "normale")}",
                        ClipboardMonitor.IsSecretClipboard() == secret);
                }

                Console.WriteLine();
                Console.WriteLine("== formati ricchi letti dalla clipboard vera ==");

                const string frag = "<b>perché</b> però €20";
                Set(o =>
                {
                    o.SetData(DataFormats.UnicodeText, "perché però €20");
                    o.SetData(DataFormats.Html, CfHtml.Build(frag));
                    o.SetData(DataFormats.Rtf, @"{\rtf1\ansi\b perche\b0}");
                });
                var got = monitor.TryReadClipboard();
                Check("HTML e RTF ritrovati, frammento intatto",
                    got?.Html == frag && got.Rtf!.StartsWith(@"{\rtf1") && got.Text == "perché però €20",
                    got?.Html ?? "—");

                // Andata e ritorno completo: si posa un payload ricco e si rilegge.
                monitor.ApplyToClipboard(ClipboardPayload.FromRichText("testo", frag, null));
                var again = monitor.TryReadClipboard();
                Check("payload ricco posato e riletto identico", again?.Html == frag && again.Text == "testo");
            }
            finally
            {
                // Si rimette dentro cio' che c'era: la clipboard e' di chi lavora.
                try
                {
                    if (saved != null) Clipboard.SetText(saved);
                    else Clipboard.Clear();
                }
                catch { }
                monitor.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine($"{ok} controlli superati, {ko} falliti.");
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return ko == 0 ? 0 : 1;
    }

    /// <summary>Posa un DataObject con qualche tentativo: la clipboard e' contesa e il primo colpo puo' fallire.</summary>
    private static void Set(Action<DataObject> fill)
    {
        var data = new DataObject();
        fill(data);
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(data, copy: true); return; }
            catch { Thread.Sleep(60); }
        }
    }
}
