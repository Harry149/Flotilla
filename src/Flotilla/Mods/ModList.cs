using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flotilla.Mods;

public sealed class ModList(string gameFile, string orderFile)
{
    static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    bool backedUp;

    public List<string> Enabled() => Read(gameFile);

    public List<string> SavedOrder() => Read(orderFile);

    public void Save(IEnumerable<(string Key, bool Enabled)> mods)
    {
        var all = mods.ToList();

        if (!backedUp && File.Exists(gameFile))
        {
            File.Copy(gameFile, gameFile + ".bak", overwrite: true);
            backedUp = true;
        }
        Write(gameFile, all.Where(m => m.Enabled).Select(m => m.Key));
        Write(orderFile, all.Select(m => m.Key));
        SortLauncher(all.Select(m => m.Key).ToList());
    }

    void SortLauncher(List<string> keys)
    {
        var path = Path.Combine(Path.GetDirectoryName(gameFile)!, "Launcher", "launcherdata");
        if (!File.Exists(path)) return;

        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return; }
        if (root?["modList"] is not JsonArray mods) return;

        int Rank(JsonNode? mod)
        {
            var index = keys.FindIndex(key => Keys.Equals(key, mod?["modName"]?.GetValue<string>()));
            return index < 0 ? int.MaxValue : index;
        }

        var sorted = mods.OrderBy(Rank).ToList();
        mods.Clear();
        foreach (var mod in sorted) mods.Add(mod);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
        File.Move(temporary, path, overwrite: true);
    }

    public static List<(string Key, bool Enabled)> Merge(IReadOnlyList<string> saved, IReadOnlyList<string> enabled, IEnumerable<string> installed)
    {
        var on = new HashSet<string>(enabled, Keys);
        var present = installed.ToList();
        var exists = new HashSet<string>(present, Keys);
        var placed = new HashSet<string>(Keys);
        var parked = new Dictionary<string, List<string>>(Keys);
        var anchor = "";

        foreach (var key in saved)
        {
            if (on.Contains(key))
            {
                anchor = key;
                continue;
            }
            if (!exists.Contains(key) || !placed.Add(key)) continue;

            if (!parked.TryGetValue(anchor, out var waiting)) parked[anchor] = waiting = [];
            waiting.Add(key);
        }

        var result = new List<(string Key, bool Enabled)>();
        void Unpark(string after)
        {
            if (parked.TryGetValue(after, out var waiting)) result.AddRange(waiting.Select(key => (key, false)));
        }

        Unpark("");
        foreach (var key in enabled)
        {
            if (!placed.Add(key)) continue;
            result.Add((key, true));
            Unpark(key);
        }
        result.AddRange(present.Where(placed.Add).Select(key => (key, false)));
        return result;
    }

    static List<string> Read(string path) => File.Exists(path)
        ? File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(Keys).ToList()
        : [];

    static void Write(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var newline = existing.Contains('\n') && !existing.Contains("\r\n") ? "\n" : "\r\n";

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, string.Join(newline, lines), Utf8);
        File.Move(temporary, path, overwrite: true);
    }
}
