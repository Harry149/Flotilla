using System.IO;
using System.IO.Pipes;
using System.Windows;
using Microsoft.Win32;

namespace Flotilla;

public static class Links
{
    const string Pipe = "Flotilla.Links";
    const string Scheme = "flotilla";
    public const string Handover = "--handover";

    static Mutex? single;

    public static string? Pending { get; set; }

    public static event Action<string>? Received;

    public static bool TakeOver(string[] args)
    {
        Pending = args.FirstOrDefault(a => a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));
        single = new Mutex(true, @"Local\Flotilla.Single", out var first);
        if (first || args.Contains(Handover)) return true;

        try
        {
            using var client = new NamedPipeClientStream(".", Pipe, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(Pending ?? "show");
        }
        catch (Exception e) when (e is IOException or TimeoutException) { }
        return false;
    }

    public static void Listen() => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(Pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync() is { } message)
                    Application.Current?.Dispatcher.BeginInvoke(() => Received?.Invoke(message));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(1000);
            }
        }
    });

    public static void Register()
    {
        if (!Updater.CanReplaceItself || Environment.ProcessPath is not { } exe) return;
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            root.SetValue("", "URL:Flotilla setup");
            root.SetValue("URL Protocol", "");
            using var icon = root.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var command = root.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }
}
