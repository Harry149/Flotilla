using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Flotilla.Steam;

public static class SteamOrder
{
    static readonly Regex Entry = new(@"\t""\d+""(\s*)\{([^{}]*)\}", RegexOptions.Compiled);
    static readonly Regex FileId = new(@"""publishedfileid""\s*""(\d+)""", RegexOptions.Compiled);
    static readonly Regex Disabled = new(@"(""disabled_locally""\s*"")[01]("")", RegexOptions.Compiled);

    public static string Arrange(string vdf, IReadOnlyList<(ulong Id, bool Enabled)> wanted)
    {
        var entries = Entry.Matches(vdf).ToList();
        if (entries.Count == 0) return vdf;

        var index = new Dictionary<ulong, (int Rank, bool Enabled)>();
        for (var i = 0; i < wanted.Count; i++) index.TryAdd(wanted[i].Id, (i, wanted[i].Enabled));

        bool Known(Match entry, out (int Rank, bool Enabled) mod)
        {
            mod = default;
            return FileId.Match(entry.Groups[2].Value) is { Success: true } id && ulong.TryParse(id.Groups[1].Value, out var value)
                && index.TryGetValue(value, out mod);
        }

        string Body(Match entry)
        {
            var body = entry.Groups[2].Value;
            if (!Known(entry, out var mod)) return body;

            var flag = mod.Enabled ? "0" : "1";
            return Disabled.IsMatch(body)
                ? Disabled.Replace(body, $"${{1}}{flag}$2")
                : body.TrimEnd() + $"\n\t\t\"disabled_locally\"\t\t\"{flag}\"\n\t";
        }

        var ordered = entries.OrderBy(e => Known(e, out var mod) ? mod.Rank : int.MaxValue)
            .Select((entry, i) => $"\t\"{i}\"{entry.Groups[1].Value}{{{Body(entry)}}}");
        var last = entries[^1];
        return vdf[..entries[0].Index] + string.Join("\n", ordered) + vdf[(last.Index + last.Length)..];
    }

    public static bool Save(IReadOnlyList<(ulong Id, bool Enabled)> wanted)
    {
        var changed = false;
        foreach (var path in SteamLibrary.SubscriptionFiles())
        {
            try
            {
                var vdf = File.ReadAllText(path);
                var arranged = Arrange(vdf, wanted);
                if (arranged == vdf) continue;

                File.WriteAllText(path + ".tmp", arranged);
                File.Move(path + ".tmp", path, overwrite: true);
                changed = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return changed;
    }

    public static DateTime? SteamStarted()
    {
        DateTime? earliest = null;
        foreach (var process in Process.GetProcessesByName("steam"))
        {
            using (process)
            {
                try
                {
                    var started = process.StartTime.ToUniversalTime();
                    if (earliest is null || started < earliest) earliest = started;
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
        return earliest;
    }
}
