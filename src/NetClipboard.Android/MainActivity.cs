using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using NetClipboard.Droid.Services;

namespace NetClipboard.Droid;

/// <summary>
/// L'unica schermata dell'applicazione. Non tiene niente di importante: il
/// trasporto vive nel servizio, che sopravvive alle rotazioni dello schermo e
/// alla chiusura di questa finestra. Avalonia la mette in piedi
/// <see cref="MainApplication"/>.
/// </summary>
[Activity(
    Label = "@string/app_name",
    Theme = "@style/NetClipboardTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int RequestNotifications = 1;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Da Android 13 la notifica va chiesta, e senza notifica non c'e'
        // servizio in primo piano: si chiede prima di avviarlo.
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, RequestNotifications);
            return;
        }

        NetClipboardService.Ensure(this);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // Anche se la notifica viene negata si prova ad avviare: il sistema la
        // mostrera' comunque per un servizio in primo piano, e l'alternativa
        // sarebbe un'applicazione che non fa niente senza dire perche'.
        if (requestCode == RequestNotifications) NetClipboardService.Ensure(this);
    }
}
