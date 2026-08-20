using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using NetClipboard.Core;

namespace NetClipboard.Droid;

/// <summary>
/// L'oggetto applicazione di Android, ed è qui che Avalonia viene messa in piedi.
///
/// Non nell'attività: da Avalonia 12 l'avvio sta nella classe <c>Application</c>,
/// che nasce una volta sola per processo. L'attività invece viene distrutta e
/// ricreata a ogni rotazione dello schermo, e con essa si portava dietro
/// l'inizializzazione del framework.
///
/// L'attributo <c>[Application]</c> non porta parametri di proposito: nome,
/// backup e le altre proprietà stanno in <c>Properties/AndroidManifest.xml</c>,
/// e dichiararle in due posti significherebbe, prima o poi, dichiararle diverse.
/// </summary>
[Application]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // La lingua si sceglie prima che compaia qualunque testo.
        L.Init();
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
