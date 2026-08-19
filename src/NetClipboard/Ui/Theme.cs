using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetClipboard.Ui;

/// <summary>
/// Palette unica dell'applicazione, in due versioni: scura e chiara.
///
/// La versione in uso la decide Windows (Impostazioni > Personalizzazione >
/// Colori > "Modalita' app"), non l'utente dentro l'app: una clipboard vive in
/// mezzo alle altre finestre, e una finestra che ignora il tema del sistema si
/// vede subito che non ci appartiene.
///
/// I colori sono PROPRIETA', non costanti: ogni disegno li rilegge al momento,
/// cosi' un cambio di tema a programma aperto si vede senza riavviare. Le
/// finestre si agganciano con <see cref="Attach"/> e si ridipingono da sole.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    // Barra del titolo scura: attributo 20 da Windows 10 2004 in poi, 19 sulle
    // build precedenti. Chiamarli entrambi non fa danno: quello sbagliato fallisce.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Vero se Windows sta usando la modalita' scura per le app.</summary>
    public static bool IsDark { get; private set; } = ReadSystemDark();

    /// <summary>
    /// Scatta quando il tema di sistema cambia. Arriva dal thread di
    /// <see cref="SystemEvents"/>: chi si iscrive deve riportarsi sulla UI
    /// (<see cref="Attach"/> lo fa gia').
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// Da chiamare una volta all'avvio, prima di costruire qualunque finestra:
    /// memorizza il tema corrente e si mette in ascolto dei cambi di impostazione
    /// di Windows.
    /// </summary>
    public static void Init()
    {
        IsDark = ReadSystemDark();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    // ----- Colori -----

    private static Color Pick(Color dark, Color light) => IsDark ? dark : light;

    /// <summary>Fondo della finestra.</summary>
    public static Color Bg => Pick(Color.FromArgb(26, 26, 32), Color.FromArgb(246, 246, 250));

    /// <summary>Fondo di una scheda/riga appoggiata sul fondo della finestra.</summary>
    public static Color Card => Pick(Color.FromArgb(38, 38, 47), Color.FromArgb(255, 255, 255));

    /// <summary>Fascia dell'intestazione.</summary>
    public static Color HeaderBg => Pick(Color.FromArgb(33, 33, 41), Color.FromArgb(238, 238, 244));

    /// <summary>Riga sotto il puntatore.</summary>
    public static Color Hover => Pick(Color.FromArgb(48, 48, 60), Color.FromArgb(234, 234, 242));

    /// <summary>Riga selezionata.</summary>
    public static Color Sel => Pick(Color.FromArgb(52, 50, 74), Color.FromArgb(232, 228, 252));

    public static Color TextMain => Pick(Color.FromArgb(238, 238, 244), Color.FromArgb(26, 26, 32));
    public static Color TextMuted => Pick(Color.FromArgb(150, 152, 165), Color.FromArgb(102, 104, 118));

    /// <summary>Testo di cio' che e' passato: usato, scaduto, non piu' utilizzabile.</summary>
    public static Color TextSpent => Pick(Color.FromArgb(104, 106, 118), Color.FromArgb(158, 160, 172));

    public static Color Accent => Pick(Color.FromArgb(120, 92, 245), Color.FromArgb(102, 72, 232));
    public static Color AccentAlt => Pick(Color.FromArgb(56, 180, 220), Color.FromArgb(28, 152, 198));

    /// <summary>Testo sopra una superficie di accento (sempre chiaro, in entrambi i temi).</summary>
    public static Color OnAccent => Color.White;

    public static Color Divider => Pick(Color.FromArgb(48, 48, 59), Color.FromArgb(222, 222, 230));

    public static Color ButtonFace => Pick(Color.FromArgb(58, 58, 70), Color.FromArgb(232, 232, 238));
    public static Color ButtonText => Pick(Color.FromArgb(240, 240, 246), Color.FromArgb(30, 30, 38));
    public static Color ButtonDisabledFace => Pick(Color.FromArgb(50, 50, 60), Color.FromArgb(238, 238, 242));

    /// <summary>Fondo dei campi di immissione e delle liste di sistema.</summary>
    public static Color Field => Pick(Color.FromArgb(40, 40, 49), Color.FromArgb(255, 255, 255));

    /// <summary>Pista vuota di una barra di avanzamento o di un anello.</summary>
    public static Color Track => Pick(Color.FromArgb(48, 48, 60), Color.FromArgb(226, 226, 234));

    public static Color Success => Pick(Color.FromArgb(90, 210, 130), Color.FromArgb(24, 140, 78));
    public static Color Info => Pick(Color.FromArgb(120, 200, 255), Color.FromArgb(20, 108, 186));
    public static Color Warn => Pick(Color.FromArgb(240, 170, 70), Color.FromArgb(186, 112, 16));

    /// <summary>Pulsante che porta avanti l'azione principale di un dialogo.</summary>
    public static Color Primary => Pick(Color.FromArgb(30, 120, 200), Color.FromArgb(24, 104, 180));

    /// <summary>Estremi del gradiente caldo usato per i contenuti "file".</summary>
    public static Color FileWarmA => Pick(Color.FromArgb(244, 176, 66), Color.FromArgb(250, 178, 62));
    public static Color FileWarmB => Pick(Color.FromArgb(222, 132, 40), Color.FromArgb(232, 138, 36));

    /// <summary>Estremi del gradiente freddo usato per i contenuti "testo".</summary>
    public static Color TextKindA => Pick(Color.FromArgb(70, 120, 235), Color.FromArgb(78, 126, 240));
    public static Color TextKindB => Pick(Color.FromArgb(60, 90, 200), Color.FromArgb(58, 92, 208));

    /// <summary>Estremi del gradiente usato per i contenuti di tipo ignoto.</summary>
    public static Color OtherKindA => Pick(Color.FromArgb(60, 170, 120), Color.FromArgb(52, 168, 116));
    public static Color OtherKindB => Pick(Color.FromArgb(40, 140, 100), Color.FromArgb(34, 138, 96));

    /// <summary>
    /// Luminosita' delle tinte derivate da un identificativo (avatar dei peer):
    /// piu' scure sul tema chiaro, cosi' il testo bianco sopra si legge sempre.
    /// </summary>
    public static double AvatarLightness => IsDark ? 0.48 : 0.42;

    // ----- Applicazione alle finestre -----

    /// <summary>
    /// Aggancia una finestra al tema: applica subito i colori e li riapplica se
    /// Windows cambia modalita'. Il collegamento si scioglie da solo quando la
    /// finestra viene distrutta.
    /// </summary>
    public static void Attach(Form form, Action apply)
    {
        apply();
        void Refresh()
        {
            if (form.IsDisposed) return;
            apply();
            ApplyWindowChrome(form);
            form.Invalidate(true);
        }
        void OnChanged()
        {
            if (form.IsDisposed) return;
            // L'avviso arriva dal thread di SystemEvents: toccare i controlli da
            // li' sarebbe un accesso incrociato.
            if (form.IsHandleCreated && form.InvokeRequired)
            {
                try { form.BeginInvoke(Refresh); } catch (ObjectDisposedException) { }
                return;
            }
            Refresh();
        }
        Changed += OnChanged;
        form.Disposed += (_, _) => Changed -= OnChanged;
        form.HandleCreated += (_, _) => ApplyWindowChrome(form);
        if (form.IsHandleCreated) ApplyWindowChrome(form);
    }

    /// <summary>Colori di base per una finestra fatta di controlli di sistema.</summary>
    public static void ApplyToControls(Control root)
    {
        root.BackColor = Bg;
        root.ForeColor = TextMain;
        foreach (Control c in root.Controls)
            StyleControl(c);
    }

    private static void StyleControl(Control c)
    {
        switch (c)
        {
            case TextBox or NumericUpDown or ComboBox or ListBox:
                c.BackColor = Field;
                c.ForeColor = TextMain;
                break;
            case ListView lv:
                lv.BackColor = Field;
                lv.ForeColor = TextMain;
                break;
            case Button b:
                // Piatto in entrambi i temi: il pulsante di sistema in modalita'
                // scura resta chiaro sulle build dove il supporto manca.
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = ButtonFace;
                b.ForeColor = ButtonText;
                b.FlatAppearance.BorderColor = Divider;
                b.FlatAppearance.BorderSize = 1;
                break;
            case CheckBox or RadioButton or Label:
                c.ForeColor = TextMain;
                break;
        }

        foreach (Control child in c.Controls)
            StyleControl(child);
    }

    /// <summary>Barra del titolo in tinta con il tema (le finestre senza bordo la ignorano).</summary>
    public static void ApplyWindowChrome(Form form)
    {
        if (!form.IsHandleCreated) return;
        var on = IsDark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
        }
        catch (DllNotFoundException) { }   // dwmapi assente: resta la barra di sistema
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>Menu della tray disegnato con la palette dell'app invece che con quella di sistema.</summary>
    public static ToolStripRenderer CreateMenuRenderer() => new MenuRenderer();

    // ----- Lettura del tema di sistema -----

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // Il valore dice se le app usano il tema CHIARO: 0 = scuro.
            return key?.GetValue(AppsUseLightTheme) is int v && v == 0;
        }
        catch
        {
            return false; // registro illeggibile: il chiaro e' il tema predefinito di Windows
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // L'evento arriva su un thread suo e per molte impostazioni diverse:
        // rileggiamo il registro e avvisiamo solo se il tema e' cambiato davvero.
        var dark = ReadSystemDark();
        if (dark == IsDark) return;
        IsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>
    /// Menu con i colori dell'app: il renderer di sistema in modalita' scura
    /// resta chiaro sulle build che non supportano il tema scuro nei ToolStrip.
    /// </summary>
    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? TextMain : TextSpent;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? TextSpent : TextMain;
            base.OnRenderArrow(e);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Card;
        public override Color MenuBorder => Divider;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Card;
        public override Color MenuItemPressedGradientEnd => Card;
        public override Color ImageMarginGradientBegin => Card;
        public override Color ImageMarginGradientMiddle => Card;
        public override Color ImageMarginGradientEnd => Card;
        public override Color SeparatorDark => Divider;
        public override Color SeparatorLight => Divider;
        public override Color CheckBackground => Sel;
        public override Color CheckSelectedBackground => Sel;
        public override Color CheckPressedBackground => Sel;
    }
}
