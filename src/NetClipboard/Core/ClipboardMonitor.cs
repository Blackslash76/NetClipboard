using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.IO;
using System.Runtime.InteropServices;

namespace NetClipboard.Core;

/// <summary>
/// Finestra nascosta che ascolta le modifiche della clipboard di Windows
/// (AddClipboardFormatListener / WM_CLIPBOARDUPDATE) e ospita l'hotkey globale
/// per aprire la cronologia.
///
/// Anti-loop: quando applichiamo un contenuto ricevuto/scelto, la clipboard
/// cambia e riscatterebbe l'evento; sopprimiamo tramite hash del contenuto + una
/// breve finestra temporale (evita il ping-pong).
/// </summary>
public sealed class ClipboardMonitor : Form
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0xB0B0;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly AppConfig _config;

    /// <summary>DeviceId da usare come proprietario delle offerte file (impostato dal TrayContext).</summary>
    public string OwnerDeviceId = "";

    private string? _suppressHash;
    private DateTime _suppressUntilUtc = DateTime.MinValue;
    private bool _listenerAdded;
    private bool _hotkeyAdded;

    public event Action<ClipboardPayload>? ClipboardChanged;
    public event Action? HistoryHotkeyPressed;

    public ClipboardMonitor(AppConfig config)
    {
        _config = config;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-4000, -4000);
        Size = new Size(1, 1);
        Opacity = 0;
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _listenerAdded = AddClipboardFormatListener(Handle);
        // Win+Alt+V: vicino alla classica Win+V (che è riservata dal sistema).
        _hotkeyAdded = RegisterHotKey(Handle, HotkeyId, MOD_WIN | MOD_ALT | MOD_NOREPEAT, 0x56);
        Log.Write(_hotkeyAdded
            ? "[Hotkey] Win+Alt+V registrata"
            : "[Hotkey] registrazione Win+Alt+V fallita (forse già in uso)");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_listenerAdded)
            RemoveClipboardFormatListener(Handle);
        if (_hotkeyAdded)
            UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_CLIPBOARDUPDATE:
                OnClipboardUpdate();
                break;
            case WM_HOTKEY when m.WParam.ToInt32() == HotkeyId:
                HistoryHotkeyPressed?.Invoke();
                break;
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Impronta dell'ultimo contenuto propagato, e quando. Serve a non ripetere
    /// la stessa copia (vedi <see cref="BurstWindow"/>).
    /// </summary>
    private string? _lastSentFingerprint;
    private DateTime _lastSentAtUtc = DateTime.MinValue;

    /// <summary>
    /// Quanto dura la raffica di eventi che una sola copia puo' produrre.
    ///
    /// Un "copia" non e' un evento solo: chi copia scrive la clipboard in piu'
    /// passaggi (testo, poi HTML, poi altro) e Windows avvisa a ogni passaggio.
    /// Lo stesso contenuto partiva cosi' due o tre volte per una copia sola. Fra
    /// PC non si vedeva — chi riceve deduplica in cronologia e riscrivere gli
    /// stessi appunti non si nota — ma sul telefono diventava una notifica per
    /// ogni invio, e lo stesso spreco di rete c'era comunque.
    ///
    /// Breve di proposito: oltre questa finestra una ricopiatura identica torna a
    /// propagarsi, perche' puo' essere voluta (un dispositivo tornato in linea
    /// nel frattempo non aveva ricevuto niente).
    /// </summary>
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(3);

    private void OnClipboardUpdate()
    {
        if (DateTime.UtcNow < _suppressUntilUtc)
            return;

        var payload = TryReadClipboard();
        if (payload == null)
            return;

        if (_suppressHash != null && payload.ContentHash() == _suppressHash)
            return;

        // L'impronta e' sui byte COMPLETI, non su ContentHash(): quello ignora la
        // formattazione di proposito, e qui ci servirebbe il contrario — ricopiare
        // lo stesso paragrafo in grassetto e' un contenuto nuovo da propagare.
        var fingerprint = Convert.ToHexString(SHA256.HashData(payload.Serialize()));
        var now = DateTime.UtcNow;
        if (fingerprint == _lastSentFingerprint && now - _lastSentAtUtc < BurstWindow)
            return;

        _lastSentFingerprint = fingerprint;
        _lastSentAtUtc = now;

        ClipboardChanged?.Invoke(payload);
    }

    /// <summary>
    /// Formati che i gestori di password mettono sulla clipboard per dire "questo
    /// non va ne' registrato ne' propagato". Sono la convenzione con cui KeePass,
    /// 1Password, Bitwarden e la cronologia di Windows si mettono d'accordo: chi
    /// legge la clipboard e' tenuto a guardarli, non e' un dettaglio facoltativo.
    ///
    /// I primi due sono bandiere: la loro sola presenza vieta. Gli altri due sono
    /// DWORD, e vietano quando valgono 0.
    /// </summary>
    private const string FmtViewerIgnore = "Clipboard Viewer Ignore";
    private const string FmtExcludeMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    private const string FmtCanIncludeHistory = "CanIncludeInClipboardHistory";
    private const string FmtCanUploadCloud = "CanUploadToCloudClipboard";

    /// <summary>
    /// True se chi ha copiato ha dichiarato il contenuto riservato.
    ///
    /// Senza questo controllo una password copiata da un gestore partiva in rete
    /// verso tutti i dispositivi accoppiati e finiva in chiaro nella cronologia su
    /// disco, dove restava giorni: la perdita di segreti piu' concreta che l'app
    /// potesse causare, e per giunta senza che l'utente facesse nulla di sbagliato.
    ///
    /// Nel dubbio si tace: se i formati non sono leggibili si risponde di si'.
    /// Perdere una copia e' una seccatura, spargere una password no.
    /// </summary>
    public static bool IsSecretClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return false;

            var formats = data.GetFormats(autoConvert: false);
            if (formats.Any(f => string.Equals(f, FmtViewerIgnore, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(f, FmtExcludeMonitor, StringComparison.OrdinalIgnoreCase)))
                return true;

            foreach (var name in new[] { FmtCanIncludeHistory, FmtCanUploadCloud })
            {
                if (!formats.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                    continue; // assente: nessun divieto, vale il comportamento normale
                if (ReadDword(data, name) != 1)
                    return true; // 0, oppure illeggibile: si tratta come divieto
            }
            return false;
        }
        catch (Exception ex)
        {
            // La clipboard e' una risorsa contesa: se un altro processo la tiene
            // aperta non si riesce nemmeno a elencare i formati. Rispondere "non
            // e' segreto" qui vorrebbe dire far passare proprio il caso peggiore.
            Debug.WriteLine($"[Clipboard] formati non leggibili: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Legge un DWORD da un formato personalizzato. WinForms consegna i formati
    /// che non conosce come <see cref="MemoryStream"/> sull'HGLOBAL; alcune
    /// applicazioni passano direttamente un byte[]. -1 = non leggibile.
    /// </summary>
    private static int ReadDword(IDataObject data, string format)
    {
        try
        {
            var raw = data.GetData(format, autoConvert: false);
            var bytes = raw switch
            {
                MemoryStream ms => ms.ToArray(),
                byte[] b => b,
                _ => null,
            };
            if (bytes is not { Length: >= 4 }) return -1;
            return BitConverter.ToInt32(bytes, 0);
        }
        catch { return -1; }
    }

    /// <summary>Legge la clipboard corrente. Da chiamare sul thread UI (STA).</summary>
    public ClipboardPayload? TryReadClipboard()
    {
        // Prima di guardare qualunque contenuto: se e' roba di un gestore di
        // password non deve nemmeno essere letta, tanto meno copiata altrove.
        if (IsSecretClipboard())
        {
            Debug.WriteLine("[Clipboard] contenuto marcato come riservato: ignorato");
            return null;
        }

        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList();
                var paths = new List<string>();
                foreach (var p in list)
                    if (!string.IsNullOrEmpty(p))
                        paths.Add(p);
                if (paths.Count > 0)
                {
                    // Solo metadati (offer): i byte partono su richiesta.
                    var offer = FileOffer.FromPaths(paths, OwnerDeviceId, _config.DisplayName);
                    if (offer != null)
                        return ClipboardPayload.FromOffer(offer);
                    return null;
                }
            }

            if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img != null)
                {
                    using var ms = new MemoryStream();
                    img.Save(ms, ImageFormat.Png);
                    return ClipboardPayload.FromImage(ms.ToArray());
                }
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                    return ClipboardPayload.FromRichText(text, ReadHtml(), ReadRtf());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipboard] lettura fallita: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Frammento HTML sulla clipboard, se c'e'. Si legge il CF_HTML e se ne tiene
    /// solo il frammento: l'intestazione la ricostruira' chi incolla.
    /// </summary>
    private static string? ReadHtml()
    {
        try
        {
            return Clipboard.ContainsText(TextDataFormat.Html)
                ? CfHtml.ExtractFragment(Clipboard.GetText(TextDataFormat.Html))
                : null;
        }
        catch { return null; } // la formattazione e' un di piu': se non si legge, pazienza
    }

    private static string? ReadRtf()
    {
        try
        {
            var rtf = Clipboard.ContainsText(TextDataFormat.Rtf) ? Clipboard.GetText(TextDataFormat.Rtf) : null;
            return string.IsNullOrEmpty(rtf) ? null : rtf;
        }
        catch { return null; }
    }

    /// <summary>Applica testo/immagine alla clipboard sopprimendo l'eco. Thread UI.</summary>
    public void ApplyToClipboard(ClipboardPayload payload)
    {
        Suppress(payload.ContentHash());
        SetWithRetry(() =>
        {
            switch (payload.Kind)
            {
                case PayloadKind.Text:
                    SetText(payload);
                    break;
                case PayloadKind.Image:
                    using (var ms = new MemoryStream(payload.ImagePng ?? Array.Empty<byte>()))
                    using (var img = Image.FromStream(ms))
                        Clipboard.SetImage(img);
                    break;
            }
        });
    }

    /// <summary>Posa dei percorsi file/cartelle (gia' materializzati) in clipboard. Thread UI.</summary>
    public void ApplyFilesToClipboard(IReadOnlyList<string> paths)
    {
        Suppress(null);
        SetWithRetry(() =>
        {
            var col = new StringCollection();
            col.AddRange(paths.ToArray());
            Clipboard.SetFileDropList(col);
        });
    }

    /// <summary>
    /// Posa il testo con la formattazione che aveva, quando c'e'.
    ///
    /// Un solo <see cref="DataObject"/> con tutti i formati insieme: chi incolla
    /// sceglie il piu' ricco che sa leggere, e chi sa leggere solo il testo trova
    /// comunque il testo. Metterli con chiamate separate azzererebbe la clipboard
    /// ogni volta, lasciando solo l'ultimo.
    /// </summary>
    private static void SetText(ClipboardPayload payload)
    {
        var text = payload.Text ?? "";
        if (!payload.HasRichText)
        {
            Clipboard.SetText(text);
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        if (payload.Html != null) data.SetData(DataFormats.Html, CfHtml.Build(payload.Html));
        if (payload.Rtf != null) data.SetData(DataFormats.Rtf, payload.Rtf);
        // copy: true — il contenuto deve restare in clipboard anche dopo che l'app
        // si chiude, come per qualunque copia normale.
        Clipboard.SetDataObject(data, copy: true);
    }

    private void Suppress(string? hash)
    {
        _suppressHash = hash;
        _suppressUntilUtc = DateTime.UtcNow.AddMilliseconds(700);
    }

    private static void SetWithRetry(Action set)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { set(); return; }
            catch (Exception ex) { last = ex; Thread.Sleep(60); }
        }
        Debug.WriteLine($"[Clipboard] scrittura fallita: {last?.Message}");
    }
}
