using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Flotilla;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnCrash;
        base.OnStartup(e);
    }

    static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        File.AppendAllText(Path.Combine(Paths.AppData, "errors.log"), $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        MessageBox.Show(e.Exception.Message, "Flotilla ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
