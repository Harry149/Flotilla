using System.Text.RegularExpressions;

namespace Flotilla.Mods;

public sealed record GameLog(string? GameVersion, IReadOnlyDictionary<string, List<string>> Errors, int Unattributed)
{
    const int PerMod = 5;

    static readonly Regex Version = new(@"^Game version:\s*(.+)$", RegexOptions.Compiled);
    static readonly Regex Compile = new(@"\.cs\(\d+,\d+\): error (CS\d+): (.+)$", RegexOptions.Compiled);
    static readonly Regex LocalMod = new(@"[\\/]Mods[\\/]([^\\/]+)[\\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SteamMod = new(@"[\\/]494840[\\/](\d+)[\\/]", RegexOptions.Compiled);
    static readonly Regex Exception = new(@"^[\w.]*Exception\b", RegexOptions.Compiled);

    public static GameLog Parse(IEnumerable<string> lines)
    {
        string? version = null;
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unattributed = 0;

        foreach (var line in lines)
        {
            if (version is null && Version.Match(line) is { Success: true } found)
            {
                version = found.Groups[1].Value.Trim();
                continue;
            }

            var compile = Compile.Match(line);
            var failed = compile.Success || line.Contains("Exception", StringComparison.Ordinal) || line.Contains("error", StringComparison.OrdinalIgnoreCase);
            if (!failed) continue;

            var key = SteamMod.Match(line) is { Success: true } steam ? ModFolder.SteamPrefix + steam.Groups[1].Value
                : LocalMod.Match(line) is { Success: true } local ? local.Groups[1].Value
                : null;

            if (key is null)
            {
                if (Exception.IsMatch(line)) unattributed++;
                continue;
            }

            var message = compile.Success ? $"it failed to compile ({compile.Groups[1].Value}: {compile.Groups[2].Value.Trim()})" : line.Trim();
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = [];
            if (list.Count < PerMod && !list.Contains(message)) list.Add(message);
        }

        return new GameLog(version, errors, unattributed);
    }

    public static bool Supports(IEnumerable<string> supported, string game)
    {
        var listed = supported.Select(Normalise).Where(v => v.Length > 0).ToList();
        if (listed.Count == 0) return true;

        var exact = Normalise(game);
        var release = exact.Split(' ')[0];
        return listed.Any(v => v == exact || v == release);
    }

    static string Normalise(string version) =>
        Regex.Replace(Regex.Replace(version.Split('\n')[0], @"\([^)]*\)", " "), @"\s+", " ").Trim().ToLowerInvariant();
}
