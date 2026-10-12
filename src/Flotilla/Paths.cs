using System.IO;

namespace Flotilla;

public static class Paths
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string AppData { get; } = Directory.CreateDirectory(Path.Combine(Local, "Flotilla")).FullName;
    public static string DefaultGame { get; } = Path.Combine(Path.GetDirectoryName(Local)!, "LocalLow", "Deep Water Studio", "UBOAT");
    public static string Game { get; } = Settings.Load().DataFolder is { Length: > 0 } custom && Directory.Exists(custom) ? custom : DefaultGame;
    public static string Mods => Path.Combine(Game, "Mods");
    public static string ModList => Path.Combine(Game, "modlist.txt");
    public static string GameLog =>
        Directory.Exists(Game)
            ? new DirectoryInfo(Game).EnumerateFiles("output_log*.txt")
                .Where(f => !f.Name.Contains("(previous)", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName ?? Path.Combine(Game, "output_log.txt")
            : Path.Combine(Game, "output_log.txt");
    public static IEnumerable<string> Caches => ["Cache", "Temp", "Data Sheets"];
}
