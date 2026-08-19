using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using NetClipboard;
using NetClipboard.Core;
using NetClipboard.Core.Security;
using NetClipboard.Net;
using NetClipboard.Ui;

namespace NetClipboard.Tools.UiShot;

/// <summary>
/// Fotografa le finestre dell'app, in tema chiaro e scuro, su file PNG.
///
/// Le finestre non compaiono mai davanti a chi sta lavorando: si aprono fuori
/// dallo schermo e senza prendere il fuoco, e l'immagine si prende con PrintWindow
/// (PW_RENDERFULLCONTENT), che legge la superficie composta da DWM. DrawToBitmap
/// non andrebbe bene: i controlli disegnati da noi sono UserPaint e non rispondono
/// a WM_PRINTCLIENT, quindi verrebbero vuoti.
///
/// Nessun dato vero: cronologia, dispositivi e impostazioni sono inventati qui,
/// cosi' le immagini si possono mostrare in giro senza pensarci.
///
///   uishot &lt;cartella-di-uscita&gt; [nome-finestra ...]
/// </summary>
internal static class Program
{
    private sealed record Shot(string Name, Func<Form> Build, Action<Form>? Prepare = null);

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        var wanted = args.Skip(1).ToList();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        L.Init();

        var shots = new[]
        {
            new Shot("invia-a-molti", () => Recipient(17, 0)),
            new Shot("invia-a-due", () => Recipient(2, 0)),
            new Shot("invia-a-uno", () => Recipient(1, 0)),
            new Shot("invia-a-cartella", () => Recipient(3, 1)),
            new Shot("invia-a-nessuno", () => Recipient(17, 0, peers: 0)),
            new Shot("cronologia", History, SelectFirstRow),
            new Shot("richiesta-in-arrivo", Incoming),
            new Shot("codice-pairing", Sas),
            new Shot("trasferimento", Transfer, f => ((TransferForm)f).Report("bilancio-2026.xlsx", 1_800_000)),
            new Shot("impostazioni", Settings),
            new Shot("dispositivi", Devices, FillDevices),
        };

        foreach (var dark in new[] { true, false })
        {
            ForceTheme(dark);
            foreach (var shot in shots)
            {
                if (wanted.Count > 0 && !wanted.Contains(shot.Name)) continue;
                var path = Path.Combine(outDir, $"{shot.Name}-{(dark ? "scuro" : "chiaro")}.png");
                Capture(shot.Build(), path, shot.Prepare);
                Console.WriteLine(path);
            }
        }
        return 0;
    }

    /// <summary>Il tema lo decide Windows: qui lo forziamo per fotografarli entrambi.</summary>
    private static void ForceTheme(bool dark)
    {
        var prop = typeof(Theme).GetProperty(nameof(Theme.IsDark), BindingFlags.Public | BindingFlags.Static);
        prop!.GetSetMethod(nonPublic: true)!.Invoke(null, new object[] { dark });
#pragma warning disable WFO5001
        Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
    }

    // ----- Le finestre, con dati inventati -----

    private static AppConfig FakeConfig() => new()
    {
        DisplayName = "PC-UFFICIO-03",
        ManualPeers = new List<string> { "192.168.1.42", "192.168.1.51" },
        HistoryVisibleRows = 4,
    };

    private static Form Recipient(int files, int dirs, int peers = 3)
    {
        var offer = new FileOffer { OfferId = Guid.NewGuid() };
        string[] names =
        {
            "Preventivo 2026 - revisione finale.pdf", "note.txt", "logo-aziendale.png",
            "bilancio.xlsx", "contratto.docx", "foto-riunione.jpg", "schema di rete.vsdx",
        };
        for (var i = 0; i < files; i++)
            offer.Entries.Add(new FileEntry
            {
                RootIndex = i,
                Size = 120_000 + i * 7_777,
                RelativePath = i < names.Length ? names[i] : $"allegato-{i + 1}.bin",
            });
        for (var i = 0; i < dirs; i++)
            offer.Entries.Add(new FileEntry { RootIndex = files + i, IsDir = true, RelativePath = $"Cartella {i + 1}" });

        return new RecipientDialog(FakePeers(peers), offer, ClipboardPayload.HumanSize(offer.TotalSize));
    }

    private static List<Peer> FakePeers(int count)
    {
        var labels = new[] { "Anna Bianchi", "PC-UFFICIO-03", "Marco Rossi" };
        var list = new List<Peer>();
        for (var i = 0; i < count; i++)
            list.Add(new Peer
            {
                DeviceId = $"device-di-prova-{i}",
                Name = labels[i % labels.Length],
                Address = IPAddress.Parse($"192.168.1.{20 + i}"),
                Port = 45654,
            });
        return list;
    }

    private static Form History()
    {
        var config = FakeConfig();
        var history = new ClipboardHistory(config);

        // La cronistoria vera sta su disco: la svuotiamo in memoria e mettiamo la
        // nostra. Non si salva nulla, quindi il file dell'utente resta intatto.
        var items = (List<HistoryItem>)typeof(ClipboardHistory)
            .GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(history)!;
        items.Clear();
        items.AddRange(new[]
        {
            new HistoryItem
            {
                Kind = PayloadKind.Text, Pinned = true, IsLocal = true,
                Preview = "https://intranet.esempio.it/pratiche/2026/marzo",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-2),
            },
            new HistoryItem
            {
                Kind = PayloadKind.Files, IsLocal = true, IsLocalOffer = true,
                FileCount = 17, TotalSize = 3_100_000,
                Preview = "17 file · 3,0 MB",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-12),
            },
            new HistoryItem
            {
                Kind = PayloadKind.Text, FromExternal = true, Origin = "Anna Bianchi",
                Preview = "Ti giro il codice fornitore: 8842-XZ",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-5),
            },
            new HistoryItem
            {
                Kind = PayloadKind.Files, FromExternal = true, Used = true, Origin = "Marco Rossi",
                FileCount = 2, DirCount = 1, TotalSize = 820_000,
                Preview = "Cartella verbali (2 file)",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-4),
            },
        });

        var form = new HistoryForm(history, config);
        form.Show();
        Invoke(form, "Reload");
        return form;
    }

    private static void SelectFirstRow(Form form)
    {
        var list = typeof(HistoryForm).GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
        list.GetType().GetProperty("SelectedIndex")!.SetValue(list, 0);
    }

    private static Form Incoming() => new IncomingOfferDialog(new IncomingOffer(
        "Anna Bianchi · PC-ANNA", "device-di-prova-0", PayloadKind.Files,
        "Preventivo 2026 - revisione finale.pdf (+16)", ScanVerdict.Clean));

    private static Form Sas() => new SasDialog(new PairingPrompt("428135", "PC-UFFICIO-03", "K7QF-2M9X-4LTB"));

    private static Form Transfer()
    {
        var form = new TransferForm(
            L.T("app.name"), "Anna Bianchi · 17 file", 3_100_000, new CancellationTokenSource());
        form.ShowAfterDelay();
        return form;
    }

    private static Form Settings() => new SettingsForm(FakeConfig());

    private static Form Devices()
    {
        var identity = DeviceIdentity.LoadOrCreate();
        // Archivio di fiducia su un percorso usa e getta: i dispositivi veri
        // dell'utente non devono finire in una fotografia.
        var trust = new TrustStore(Path.Combine(Path.GetTempPath(), "uishot-trusted.json"));
        var transport = new ClipboardTransport(FakeConfig(), identity, trust, new OfferStore());
        return new DevicesForm(identity, trust, transport);
    }

    private static void FillDevices(Form form)
    {
        // Il timer di aggiornamento riscriverebbe le liste: prima si ferma.
        var timer = (System.Windows.Forms.Timer)Field(form, "_refresh")!;
        timer.Stop();

        ((Label)Field(form, "_self")!).Text = "PC-UFFICIO-03 · impronta K7QF-2M9X-4LTB";
        Fill((ListView)Field(form, "_trusted")!,
            ("PC-CASA", "3XQM-8B1D-7WKE"), ("NOTEBOOK-FP", "T4LS-9NZC-2VHA"));
        Fill((ListView)Field(form, "_discovered")!,
            ("Anna Bianchi · PC-ANNA", "192.168.1.20"), ("Marco Rossi · PC-MR", "192.168.1.22"));

        static void Fill(ListView lv, params (string A, string B)[] rows)
        {
            lv.Items.Clear();
            foreach (var (a, b) in rows)
            {
                var item = new ListViewItem(a) { Tag = a };
                item.SubItems.Add(b);
                lv.Items.Add(item);
            }
            if (lv.Items.Count > 0) { lv.Items[0].Selected = true; lv.Items[0].Focused = true; }
        }
    }

    // ----- Meccanica della cattura -----

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);

    private static void Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, null);

    private const int SW_SHOWNOACTIVATE = 4;
    private const int PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    /// <summary>
    /// Apre la finestra fuori dallo schermo, senza attivarla, la lascia disegnare e
    /// la fotografa. Chi sta usando il PC non vede niente e non perde il fuoco.
    /// </summary>
    private static void Capture(Form form, string path, Action<Form>? prepare)
    {
        form.TopMost = false;          // niente finestre appiccicate davanti a tutto
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;

        // Trasparente invece che fuori schermo: DWM smette di comporre una finestra
        // che sta tutta fuori dai monitor, e PrintWindow restituisce una superficie
        // vuota (visto: il secondo giro di scatti veniva bianco). Con opacita' zero
        // la finestra viene composta lo stesso, ma non si vede.
        if (form.Visible) form.Hide();
        form.Opacity = 0;
        form.Location = Hidden;
        _ = form.Handle;               // crea l'handle: da qui in poi si scala e si dispone
        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        form.Location = Hidden;        // il layout puo' aver ricentrato la finestra

        // Un paio di giri di messaggi non bastano: il primo disegno arriva dopo il
        // ridimensionamento per il DPI.
        Pump(400);
        prepare?.Invoke(form);
        form.Location = Hidden;
        form.Invalidate(true);
        Pump(400);

        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(path, ImageFormat.Png);
        }

        form.Dispose();
        Pump(50);
    }

    /// <summary>
    /// Angolo dello schermo primario: la finestra e' li' ma con opacita' zero, quindi
    /// invisibile. Deve restare dentro un monitor, altrimenti DWM non la compone.
    /// </summary>
    private static Point Hidden => new(0, 0);

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }
}
