using System.Runtime.InteropServices;

namespace NetClipboard.Ui;

/// <summary>
/// Aiuti Win32 per incollare nella finestra da cui è stato aperto il popup:
/// riporta il fuoco a quella finestra e simula Ctrl+V (come fa Win+V).
/// </summary>
internal static class NativePaste
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // SendInput e non keybd_event: quest'ultima e' deprecata da vent'anni, e la sua
    // presenza in un binario non firmato e' uno dei segnali che fa alzare il
    // punteggio alle euristiche antivirus (e' l'API dei keylogger di vecchia data).
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Unione Win32 MOUSEINPUT/KEYBDINPUT/HARDWAREINPUT. Qui serve solo la tastiera,
    /// ma il membro mouse va dichiarato lo stesso: SendInput rifiuta la chiamata se
    /// cbSize non e' la dimensione della variante piu' grande (40 byte su x64, non
    /// i 32 della sola parte tastiera). L'app si pubblica win-x64, da cui l'offset 8.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    // Per trascinare una finestra senza barra del titolo.
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void SendCtrlV()
    {
        var seq = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_V, false),
            Key(VK_V, true),
            Key(VK_CONTROL, true),
        };
        SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
    };
}
