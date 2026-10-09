namespace Flotilla.Mods;

public sealed record PackEntry(string Key, ulong Manifest, string Name)
{
    public ulong? WorkshopId => ulong.TryParse(Key, out var id) ? id : null;
}

public static class ModPack
{
    const string Latest = "latest";

    public static string Write(IEnumerable<PackEntry> mods) => string.Join("\r\n",
    [
        "# Flotilla mod list. Use Import in Flotilla to install these mods at these versions and switch them on.",
        "# Load order, top to bottom: Workshop ID (or folder name), version, name.",
        .. mods.Select(m => $"{m.Key}\t{(m.Manifest == 0 ? Latest : m.Manifest.ToString())}\t{Clean(m.Name)}"),
        "",
    ]);

    public static List<PackEntry> Read(string text)
    {
        var mods = new List<PackEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Contains('\t') ? line.Split('\t', 3) : line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var key = parts[0].Trim();
            var manifest = parts.Length > 1 && ulong.TryParse(parts[1].Trim(), out var m) ? m : 0;
            var name = parts.Length > 2 && parts[2].Trim() is { Length: > 0 } title ? title : key;

            if (mods.All(e => !e.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) mods.Add(new PackEntry(key, manifest, name));
        }
        return mods;
    }

    static string Clean(string name) => name.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
