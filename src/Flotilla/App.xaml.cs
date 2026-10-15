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
        var settings = Settings.Load();
        Flotilla.UI.Themes.Apply(this, settings.Theme);
        Links.Listen();
        Links.Register();
        if (settings.SendErrorReports) _ = ErrorReports.SendPendingAsync();
        new MainWindow().Show();
    }

    static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Paths.AppData, "errors.log"), $"{DateTime.Now:u} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (Settings.Load().SendErrorReports) ErrorReports.Send(ErrorReports.Write(e.Exception));
        var message = e.Exception is FileNotFoundException && Environment.ProcessPath is { } exe && !File.Exists(exe)
            ? "Flotilla.exe was moved or deleted while it was running, often by antivirus. Close Flotilla and open it again from where it is now."
            : e.Exception.Message;
        MessageBox.Show(message, "Flotilla ran into a problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
