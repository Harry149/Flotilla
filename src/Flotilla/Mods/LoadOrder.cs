using System.IO;
using System.Text.RegularExpressions;

namespace Flotilla.Mods;

public sealed record OrderMod(string Key, string Name, string Description, ulong? WorkshopId, IReadOnlyList<ulong> Needs, IReadOnlySet<string> Sheets, bool TextOnly);

public static class LoadOrder
{
    const string Locales = "Locales.xlsx";

    static readonly Regex PatchWords = new(@"\b(patch|compat\w*|add-?on|fix)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    sealed record Rule(int Before, int After, string? BeforeReason, string? AfterReason);

    public static (List<string> Order, Dictionary<string, string> Reasons) Best(IReadOnlyList<OrderMod> mods)
    {
        var rules = new List<Rule>();
        var names = mods.Select(m => BaseName(m.Name)).ToList();
        var byId = new Dictionary<ulong, int>();
        for (var i = 0; i < mods.Count; i++)
            if (mods[i].WorkshopId is { } id) byId.TryAdd(id, i);

        for (var after = 0; after < mods.Count; after++)
        {
            foreach (var need in mods[after].Needs)
            {
                if (byId.TryGetValue(need, out var before) && before != after) rules.Add(new(before, after, $"{names[after]} needs it", null));
            }
        }

        var described = mods.Select(m => (m.Name, m.Description)).ToList();
        for (var after = 0; after < mods.Count; after++)
            foreach (var before in Bases(described, after))
                rules.Add(new(before, after, null, $"Made for {names[before]}, so it loads after it"));

        for (var after = 0; after < mods.Count; after++)
        {
            if (!mods[after].TextOnly) continue;
            for (var before = 0; before < mods.Count; before++)
                if (!mods[before].TextOnly) rules.Add(new(before, after, null, "Only changes text, so it loads last"));
        }

        for (var a = 0; a < mods.Count; a++)
        {
            for (var b = 0; b < mods.Count; b++)
            {
                var shared = mods[a].Sheets.Intersect(mods[b].Sheets, StringComparer.OrdinalIgnoreCase).Where(s => !s.Equals(Locales, StringComparison.OrdinalIgnoreCase)).ToList();
                if (shared.Count == 0 || mods[a].Sheets.Count <= mods[b].Sheets.Count) continue;
                rules.Add(new(a, b, $"Changes more data sheets than {names[b]}, so it goes first and {names[b]}'s changes to {Path.GetFileNameWithoutExtension(shared[0])} win", null));
            }
        }

        var edges = Enumerable.Range(0, mods.Count).Select(_ => new HashSet<int>()).ToList();
        var reasons = new Dictionary<string, string>();
        foreach (var rule in rules)
        {
            if (edges[rule.Before].Contains(rule.After) || Reaches(edges, rule.After, rule.Before)) continue;
            edges[rule.Before].Add(rule.After);
            if (rule.BeforeReason is { } why) reasons.TryAdd(mods[rule.Before].Key, why);
            if (rule.AfterReason is { } because) reasons.TryAdd(mods[rule.After].Key, because);
        }

        var waiting = new int[mods.Count];
        foreach (var next in edges.SelectMany(set => set)) waiting[next]++;
        var ready = new SortedSet<int>(Enumerable.Range(0, mods.Count).Where(i => waiting[i] == 0));
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var first = ready.Min;
            ready.Remove(first);
            order.Add(mods[first].Key);
            foreach (var next in edges[first])
                if (--waiting[next] == 0) ready.Add(next);
        }
        return (order, reasons);
    }

    public static string BaseName(string name) => GameLog.SplitTitle(name).Title.Trim();

    // The mods that mods[patch] was made for: its name contains theirs ("Shadow Zone Patch"), or it's named like a patch and its description names them.
    public static IEnumerable<int> Bases(IReadOnlyList<(string Name, string Description)> mods, int patch)
    {
        var name = BaseName(mods[patch].Name);
        var patchLike = PatchWords.IsMatch(mods[patch].Name);
        for (var other = 0; other < mods.Count; other++)
        {
            if (other == patch) continue;
            var otherName = BaseName(mods[other].Name);
            if (otherName.Length < 5) continue;
            if (name.Contains(otherName, StringComparison.OrdinalIgnoreCase)
                || patchLike && mods[patch].Description.Contains(otherName, StringComparison.OrdinalIgnoreCase))
                yield return other;
        }
    }

    static bool Reaches(List<HashSet<int>> edges, int from, int to)
    {
        var seen = new HashSet<int> { from };
        var stack = new Stack<int>([from]);
        while (stack.Count > 0)
        {
            var at = stack.Pop();
            if (at == to) return true;
            foreach (var next in edges[at])
                if (seen.Add(next)) stack.Push(next);
        }
        return false;
    }

    public static (HashSet<string> Sheets, bool TextOnly) Contents(string folder)
    {
        var sheets = Directory.Exists(Path.Combine(folder, "Data Sheets"))
            ? Directory.EnumerateFiles(Path.Combine(folder, "Data Sheets"), "*.xlsx", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var code = Directory.Exists(Path.Combine(folder, "Source")) || Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Any();
        var assets = new[] { "Bundles", "Textures", "Assets", "Sounds", "Models" }.Any(d => Directory.Exists(Path.Combine(folder, d)));
        var textOnly = sheets.Count == 1 && sheets.Contains(Locales) && !code && !assets;
        return (sheets, textOnly);
    }
}
