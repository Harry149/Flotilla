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
    public static string GameLog => Path.Combine(Game, "output_log.txt");
    public static IEnumerable<string> Caches => ["Cache", "Temp", "Data Sheets"];
}
