using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Flotilla.Mods;

// A short fingerprint of what UBOAT's cache was built from: the game version and the mods that are on, in order, with each one's newest file time.
public static class CacheStamp
{
    static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    public static string Of(IEnumerable<(string Key, string? Folder)> mods, string? game)
    {
        var text = new StringBuilder(game ?? "").Append('\n');
        foreach (var (key, folder) in mods) text.Append(key).Append(' ').Append(Newest(folder).Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    static DateTime Newest(string? folder)
    {
        try
        {
            return folder is not null && Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("*", Everything).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max()
                : default;
        }
        catch (IOException)
        {
            return default;
        }
    }
}
