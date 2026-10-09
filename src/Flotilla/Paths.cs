using System.IO;

namespace Flotilla;

public static class Paths
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string AppData { get; } = Directory.CreateDirectory(Path.Combine(Local, "Flotilla")).FullName;
    public static string Game { get; } = Path.Combine(Path.GetDirectoryName(Local)!, "LocalLow", "Deep Water Studio", "UBOAT");
    public static string Mods => Path.Combine(Game, "Mods");
    public static string ModList => Path.Combine(Game, "modlist.txt");
}
