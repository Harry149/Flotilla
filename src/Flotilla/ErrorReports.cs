using System.IO;
using System.Net.Http;
using System.Text;

namespace Flotilla;

public static class ErrorReports
{
    const int MaxLength = 60_000;

    static readonly string Pending = Path.Combine(Paths.AppData, "unsent reports");

    public static string Write(Exception error) => Scrub(string.Join(Environment.NewLine,
        $"Flotilla {Updater.Current}",
        $"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}",
        $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
        "",
        error.ToString()));

    public static string Scrub(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length > 2) text = text.Replace(Environment.UserName, "<user>", StringComparison.OrdinalIgnoreCase);
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    public static void Send(string report)
    {
        if (!Sailors.Enabled) return;
        _ = Task.Run(async () =>
        {
            if (!await PostAsync(report)) Keep(report);
        });
    }

    public static async Task SendPendingAsync()
    {
        if (!Sailors.Enabled || !Directory.Exists(Pending)) return;

        foreach (var file in Directory.GetFiles(Pending, "*.txt").Take(10))
        {
            try
            {
                if (!await PostAsync(await File.ReadAllTextAsync(file))) return;
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static async Task<bool> PostAsync(string report)
    {
        try
        {
            using var content = new StringContent(report, Encoding.UTF8, "text/plain");
            using var response = await Web.Client.PostAsync($"{Sailors.Api}/report", content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    static void Keep(string report)
    {
        try
        {
            Directory.CreateDirectory(Pending);
            File.WriteAllText(Path.Combine(Pending, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.txt"), report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
