using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Flotilla.Steam;

public static class SteamLibrary
{
    const string DefaultSteam = @"C:\Program Files (x86)\Steam";

    static readonly Regex LibraryPath = new("\"path\"\\s+\"([^\"]+)\"", RegexOptions.Compiled);

    public static string? WorkshopFolder()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? DefaultSteam;
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        string[] libraries = File.Exists(folders) ? [steam, .. LibraryPaths(File.ReadAllText(folders))] : [steam];

        return libraries
            .Select(library => Path.GetFullPath(Path.Combine(library, "steamapps", "workshop", "content", Workshop.AppId.ToString())))
            .FirstOrDefault(Directory.Exists);
    }

    // The manifest of each mod Steam keeps for a subscription, from Steam's record next to its Workshop folder.
    public static Dictionary<ulong, ulong> Manifests() =>
        WorkshopFolder() is { } folder
            ? SteamCmd.Manifests(SteamCmd.ReadText(Path.Combine(folder, "..", "..", $"appworkshop_{Workshop.AppId}.acf")))
            : [];

    public static IEnumerable<string> LibraryPaths(string vdf) =>
        LibraryPath.Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));
}
