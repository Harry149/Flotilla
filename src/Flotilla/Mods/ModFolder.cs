using System.IO;

namespace Flotilla.Mods;

public enum ModSource { Steam, Download, Local, Missing }

public sealed record ModFolder(string Key, ModSource Source, string? Path, Manifest? Manifest)
{
    public const string SteamPrefix = "steam:";

    public ulong? WorkshopId =>
        ulong.TryParse(Key.StartsWith(SteamPrefix) ? Key[SteamPrefix.Length..] : Key, out var id) ? id
        : Manifest is { SteamId: > 0 } ? Manifest.SteamId
        : null;

    public static ModFolder Missing(string key) => new(key, ModSource.Missing, null, null);

    public static IEnumerable<ModFolder> Scan(string mods, string? workshop)
    {
        foreach (var folder in Folders(mods))
        {
            var source = IsNumber(folder.Name) ? ModSource.Download : ModSource.Local;
            yield return new ModFolder(folder.Name, source, folder.FullName, Manifest.Read(folder.FullName));
        }

        foreach (var folder in Folders(workshop).Where(f => IsNumber(f.Name)))
        {
            yield return new ModFolder(SteamPrefix + folder.Name, ModSource.Steam, folder.FullName, Manifest.Read(folder.FullName));
        }
    }

    static IEnumerable<DirectoryInfo> Folders(string? path) =>
        path is not null && Directory.Exists(path) ? new DirectoryInfo(path).EnumerateDirectories().OrderBy(d => d.Name) : [];

    static bool IsNumber(string name) => name.Length > 0 && name.All(char.IsAsciiDigit);
}
