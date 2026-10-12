using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Flotilla;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!Links.TakeOver(e.Args))
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnCrash;
        base.OnStartup(e);
        Links.Listen();
        Links.Register();
        if (Settings.Load().SendErrorReports) _ = ErrorReports.SendPendingAsync();
        new MainWindow().Show();
    }

    static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        File.AppendAllText(Path.Combine(Paths.AppData, "errors.log"), $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        if (Settings.Load().SendErrorReports) ErrorReports.Send(ErrorReports.Write(e.Exception));
        MessageBox.Show(e.Exception.Message, "Flotilla ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
