using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing.Imaging;
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

    private void OnClipboardUpdate()
    {
        if (DateTime.UtcNow < _suppressUntilUtc)
            return;

        var payload = TryReadClipboard();
        if (payload == null)
            return;

        if (_suppressHash != null && payload.ContentHash() == _suppressHash)
            return;

        ClipboardChanged?.Invoke(payload);
    }

    /// <summary>Legge la clipboard corrente. Da chiamare sul thread UI (STA).</summary>
    public ClipboardPayload? TryReadClipboard()
    {
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
                    var offer = FileOffer.FromPaths(paths, _config.InstanceId, _config.DisplayName);
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
                    return ClipboardPayload.FromText(text);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipboard] lettura fallita: {ex.Message}");
        }
        return null;
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
                    Clipboard.SetText(payload.Text ?? "");
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
