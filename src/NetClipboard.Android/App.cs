using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using NetClipboard.Droid.Views;

namespace NetClipboard.Droid;

/// <summary>
/// L'applicazione Avalonia. Costruita in codice e non in XAML, come le finestre
/// di Windows: i testi si prendono dal catalogo con <c>L.T</c>, e un binding XAML
/// verso un metodo statico sarebbe piu' cerimonia che sostanza.
/// </summary>
public sealed class App : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new MainView();

        base.OnFrameworkInitializationCompleted();
    }
}
