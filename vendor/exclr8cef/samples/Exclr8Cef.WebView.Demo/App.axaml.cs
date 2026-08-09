using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Exclr8Cef.WebView.Demo;

public partial class App : Application
{
    /// <summary>
    /// When non-null, OnFrameworkInitializationCompleted skips auto-
    /// creating the OSR MainWindow and lets the caller assign whichever
    /// window it wants (e.g. EmbeddedHostWindow). Program sets this
    /// before SetupWithLifetime runs.
    /// </summary>
    public static Func<Avalonia.Controls.Window>? MainWindowFactory { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = MainWindowFactory is not null
                ? MainWindowFactory()
                : new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
