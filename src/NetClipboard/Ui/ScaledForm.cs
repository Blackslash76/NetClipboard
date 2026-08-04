namespace NetClipboard.Ui;

/// <summary>
/// Form che si scala da sé in base al DPI del monitor (AutoScaleMode.None): tutte le
/// misure passano da <see cref="P"/> e tutti i font sono in pixel logici scalati.
/// Così l'interfaccia resta corretta con lo scaling di Windows (125% / 150% / 200%)
/// e si riadatta se la finestra viene spostata su un monitor con DPI diverso.
/// </summary>
public abstract class ScaledForm : Form
{
    private readonly List<Font> _fonts = new();
    private float _scale = 1f;
    private bool _scaled;

    protected ScaledForm()
    {
        AutoScaleMode = AutoScaleMode.None; // scaliamo noi, manualmente
    }

    /// <summary>Fattore di scala corrente (1.0 = 96 DPI, 1.5 = 150%).</summary>
    protected float ScaleFactor => _scale;

    /// <summary>Converte una misura logica (espressa a 96 DPI) in pixel reali.</summary>
    protected int P(double v) => (int)Math.Round(v * _scale);

    /// <summary>
    /// Font con dimensione in PIXEL logici (a 96 DPI), scalato al DPI corrente.
    /// 12 px equivalgono al classico "Segoe UI 9pt". I font creati qui vengono
    /// rilasciati automaticamente al successivo cambio di DPI e alla chiusura.
    /// </summary>
    protected Font PxFont(string family, float px, FontStyle style = FontStyle.Regular)
    {
        var f = new Font(family, px * _scale, style, GraphicsUnit.Pixel);
        _fonts.Add(f);
        return f;
    }

    /// <summary>Font di base della finestra (12 px logici). Ridefinibile dalle derivate.</summary>
    protected virtual Font CreateBaseFont() => PxFont("Segoe UI", 12f);

    /// <summary>
    /// Posiziona/dimensiona i controlli usando <see cref="P"/> e <see cref="PxFont"/>.
    /// Viene rieseguita a ogni cambio di DPI: NON deve creare né aggiungere controlli,
    /// solo assegnare bounds, font e <c>ClientSize</c>.
    /// </summary>
    protected abstract void ApplyLayout();

    private void Rescale(int dpi)
    {
        var scale = dpi / 96f;
        if (_scaled && Math.Abs(scale - _scale) < 0.01f)
            return;

        var first = !_scaled;
        _scale = scale;
        _scaled = true;

        // I vecchi font restano vivi finché il nuovo layout non è applicato,
        // altrimenti un repaint intermedio userebbe un font già rilasciato.
        var previous = _fonts.ToList();
        _fonts.Clear();

        SuspendLayout();
        Font = CreateBaseFont();
        ApplyLayout();
        ClampToWorkingArea();
        ResumeLayout(performLayout: true);

        foreach (var f in previous)
            f.Dispose();

        // il layout cambia la dimensione dopo che WinForms ha già calcolato la
        // posizione iniziale: ricentriamo noi (solo al primo scaling)
        if (first && StartPosition == FormStartPosition.CenterScreen)
            CenterToScreen();
    }

    /// <summary>
    /// Con scaling molto alto su schermi piccoli la finestra potrebbe superare lo
    /// spazio disponibile: in quel caso la limitiamo e attiviamo lo scorrimento,
    /// così i pulsanti restano comunque raggiungibili.
    /// </summary>
    private void ClampToWorkingArea()
    {
        if (!IsHandleCreated)
            return;
        var wa = Screen.FromHandle(Handle).WorkingArea;
        if (Height <= wa.Height && Width <= wa.Width)
            return;
        AutoScroll = true;
        Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Rescale(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rescale(e.DeviceDpiNew);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var f in _fonts)
                f.Dispose();
            _fonts.Clear();
        }
        base.Dispose(disposing);
    }
}
