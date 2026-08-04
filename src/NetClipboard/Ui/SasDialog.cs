using NetClipboard.Net;

namespace NetClipboard.Ui;

/// <summary>
/// Chiede all'utente di confermare che il codice a 6 cifre coincide con quello
/// mostrato sull'altro PC (numeric comparison anti-intercettazione). Si auto-annulla
/// dopo 60 secondi.
/// </summary>
public sealed class SasDialog : Form
{
    private int _secondsLeft = 60;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Button _ok;

    public SasDialog(PairingPrompt prompt)
    {
        Text = "Conferma pairing";
        Icon = IconFactory.Shared;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ClientSize = new Size(380, 260);
        BackColor = Color.FromArgb(28, 28, 34);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);

        Controls.Add(new Label
        {
            Text = "Verifica il dispositivo",
            Left = 20, Top = 18, Width = 340, Height = 24,
            Font = new Font("Segoe UI Semibold", 12f),
        });
        Controls.Add(new Label
        {
            Text = $"{prompt.PeerName}\nimpronta {prompt.Fingerprint}",
            Left = 20, Top = 48, Width = 340, Height = 40,
            ForeColor = Color.Gainsboro,
        });

        var spaced = string.Join("  ", prompt.Sas.ToCharArray());
        Controls.Add(new Label
        {
            Text = spaced,
            Left = 20, Top = 96, Width = 340, Height = 54,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Consolas", 30f, FontStyle.Bold),
            ForeColor = Color.FromArgb(120, 200, 255),
        });
        Controls.Add(new Label
        {
            Text = "Conferma SOLO se lo stesso codice appare sull'altro PC.",
            Left = 20, Top = 156, Width = 340, Height = 36,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Silver,
        });

        _ok = new Button { Text = "Confermo", DialogResult = DialogResult.OK, Left = 150, Top = 210, Width = 100, Height = 30 };
        var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Left = 260, Top = 210, Width = 100, Height = 30 };
        foreach (var b in new[] { _ok, cancel })
        {
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(55, 55, 66);
        }
        _ok.BackColor = Color.FromArgb(30, 120, 200);
        Controls.Add(_ok);
        Controls.Add(cancel);
        AcceptButton = _ok;
        CancelButton = cancel;

        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            _ok.Text = $"Confermo ({_secondsLeft})";
            if (_secondsLeft <= 0)
            {
                _timer.Stop();
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
