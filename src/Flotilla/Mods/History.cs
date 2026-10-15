using System.IO;
using System.Text.Json;

namespace Flotilla.Mods;

public sealed record Snapshot(DateTime Taken, string Reason, IReadOnlyList<PackEntry> Mods);

public sealed record SetupDifference(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Versions, bool Reordered)
{
    public bool Same => Added.Count == 0 && Removed.Count == 0 && Versions.Count == 0 && !Reordered;

    public string Describe()
    {
        if (Same) return "The same as now.";

        var parts = new List<string>();
        if (Added.Count > 0) parts.Add($"{Added.Count} more on");
        if (Removed.Count > 0) parts.Add($"{Removed.Count} fewer on");
        if (Versions.Count > 0) parts.Add($"{Versions.Count} at another version");
        if (Reordered) parts.Add("a different order");
        return string.Join(", ", parts);
    }
}

public static class History
{
    const int Kept = 40;
    static readonly TimeSpan Merge = TimeSpan.FromMinutes(2);

    public static List<Snapshot> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Snapshot>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return [];
        }
    }

    public static void Save(string path, IEnumerable<Snapshot> snapshots)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshots));
        File.Move(temporary, path, overwrite: true);
    }

    public static bool Record(List<Snapshot> snapshots, Snapshot next)
    {
        if (snapshots.Count > 0 && Compare(snapshots[0].Mods, next.Mods).Same) return false;

        if (snapshots.Count > 0 && next.Taken - snapshots[0].Taken < Merge && snapshots[0].Reason == next.Reason) snapshots[0] = next;
        else snapshots.Insert(0, next);

        if (snapshots.Count > Kept) snapshots.RemoveRange(Kept, snapshots.Count - Kept);
        return true;
    }

    public static SetupDifference Compare(IReadOnlyList<PackEntry> from, IReadOnlyList<PackEntry> to)
    {
        var keys = StringComparer.OrdinalIgnoreCase;
        var before = from.ToDictionary(m => m.Key, keys);
        var after = to.ToDictionary(m => m.Key, keys);

        var added = to.Where(m => !before.ContainsKey(m.Key)).Select(m => m.Name).ToList();
        var removed = from.Where(m => !after.ContainsKey(m.Key)).Select(m => m.Name).ToList();
        var versions = to.Where(m => before.TryGetValue(m.Key, out var old) && old.Manifest != 0 && m.Manifest != 0 && old.Manifest != m.Manifest)
            .Select(m => m.Name).ToList();

        var shared = from.Select(m => m.Key).Where(after.ContainsKey).ToList();
        var reordered = !shared.SequenceEqual(to.Select(m => m.Key).Where(before.ContainsKey), keys);
        return new SetupDifference(added, removed, versions, reordered);
    }
}

public sealed record Bisect(IReadOnlyList<PackEntry> Original, IReadOnlyList<string> Suspects, IReadOnlyList<string> Testing, int Round)
{
    public static Bisect Start(IReadOnlyList<PackEntry> original, IReadOnlyList<string> suspects, Func<string, IEnumerable<string>> needs) =>
        Next(original, suspects, 1, needs);

    public int RoundsLeft => Suspects.Count <= 1 ? 0 : (int)Math.Ceiling(Math.Log2(Suspects.Count));

    public string? Culprit => Suspects.Count == 1 ? Suspects[0] : null;

    public Bisect Answer(bool stillBroken, Func<string, IEnumerable<string>> needs)
    {
        var tested = new HashSet<string>(Testing, StringComparer.OrdinalIgnoreCase);
        var left = Suspects.Where(s => tested.Contains(s) == stillBroken).ToList();
        return Next(Original, left.Count == 0 ? Suspects : left, Round + 1, needs);
    }

    public IEnumerable<string> EnabledNow()
    {
        var suspects = new HashSet<string>(Suspects, StringComparer.OrdinalIgnoreCase);
        var testing = new HashSet<string>(Testing, StringComparer.OrdinalIgnoreCase);
        return Original.Select(m => m.Key).Where(k => !suspects.Contains(k) || testing.Contains(k));
    }

    static Bisect Next(IReadOnlyList<PackEntry> original, IReadOnlyList<string> suspects, int round, Func<string, IEnumerable<string>> needs)
    {
        if (suspects.Count <= 1) return new Bisect(original, suspects, suspects, round);

        var half = suspects.Take((suspects.Count + 1) / 2).ToList();
        var testing = new List<string>(half);
        foreach (var key in half)
            foreach (var need in needs(key))
                if (!testing.Contains(need, StringComparer.OrdinalIgnoreCase) && original.Any(m => m.Key.Equals(need, StringComparison.OrdinalIgnoreCase)))
                    testing.Add(need);
        return new Bisect(original, suspects, testing, round);
    }

    public static Bisect? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Bisect>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

public static class ShareCodes
{
    const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public static string? Normalise(string input)
    {
        var text = input.Trim();
        var slash = text.LastIndexOf('/');
        if (slash >= 0) text = text[(slash + 1)..];
        text = text.ToUpperInvariant().Replace("FLT-", "").Replace("-", "").Replace(" ", "");
        return text.Length == 6 && text.All(Alphabet.Contains) ? text : null;
    }

    public static string Display(string code) => $"FLT-{code}";

    public static string Link(string code) => $"flotilla://setup/{code}";
}
