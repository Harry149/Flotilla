using System.Text;
using System.Text.RegularExpressions;

namespace Flotilla.Mods;

public enum Severity { Warning, Error }

public sealed record LogProblem(Severity Severity, string Summary, IReadOnlyList<string> Lines);

public sealed record GameLog(string? GameVersion, IReadOnlyDictionary<string, List<LogProblem>> ByMod, IReadOnlyList<LogProblem> Unattributed)
{
    const int PerMod = 10;
    const int Frames = 15;

    static readonly Regex Version = new(@"^Game version:\s*(.+)$", RegexOptions.Compiled);
    static readonly Regex Compile = new(@"\.cs\(\d+,\d+\): error (CS\d+): (.+)$", RegexOptions.Compiled);
    static readonly Regex LocalMod = new(@"[\\/]Mods[\\/]([^\\/]+)[\\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SteamMod = new(@"[\\/]494840[\\/](\d+)[\\/]", RegexOptions.Compiled);
    static readonly Regex Exception = new(@"^\s*(?:[\w.`]+\.)?\w*Exception\b(?::|\s*$)", RegexOptions.Compiled);
    static readonly Regex Frame = new(@"^\s*(?:at\s+|Rethrow as\s+|\(wrapper|\[0x|UnityEngine\.|---\s|[\w.`+<>]+:[\w<>`.]+\s*\()", RegexOptions.Compiled);
    static readonly Regex FrameType = new(@"^\s*(?:at\s+(?:\(wrapper [^)]*\)\s*)?([\w.`+<>]+)\.[\w<>`]+\s*[\[(]|([\w.`+<>]+):[\w<>`.]+\s*\()", RegexOptions.Compiled);
    static readonly Regex Tagged = new(@"^\[([^\]]{1,40})\]\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex ErrorLevel = new(@"^(ERROR|FATAL|EXCEPTION|CRITICAL)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex WarningLevel = new(@"^(WARN|WARNING)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex ErrorWords = new(@"\bfatal\b|\w*Exception\b|\berror:|\bcould not load\b|\bfailed to (load|patch|apply|initiali[sz]e|compile)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex Trouble = new(@"could not load (file or assembly|type)|TypeLoadException|MissingMethodException|MissingFieldException|ReflectionTypeLoadException|Patching exception|failed to (load|patch|apply)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static GameLog Parse(IEnumerable<string> lines, ModFingerprints? mods = null)
    {
        string? version = null;
        var byMod = new Dictionary<string, List<LogProblem>>(StringComparer.OrdinalIgnoreCase);
        var unattributed = new List<LogProblem>();
        List<string>? block = null;
        string? blockKey = null;

        void Add(string? key, LogProblem problem)
        {
            var list = key is null ? unattributed : byMod.TryGetValue(key, out var found) ? found : byMod[key] = [];
            if (list.Count < (key is null ? 50 : PerMod) && list.All(p => p.Summary != problem.Summary)) list.Add(problem);
        }

        void Close()
        {
            if (block is null) return;
            Add(blockKey, new LogProblem(Severity.Error, Shorten(block[0]), block));
            block = null;
            blockKey = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (block is not null)
            {
                if (line.Length > 0 && Frame.IsMatch(line))
                {
                    if (block.Count < Frames) block.Add(line);
                    blockKey ??= Owner(line, mods);
                    continue;
                }
                Close();
            }

            if (version is null && Version.Match(line) is { Success: true } found)
            {
                version = found.Groups[1].Value.Trim();
                continue;
            }

            if (Compile.Match(line) is { Success: true } compile)
            {
                Add(PathOwner(line), new LogProblem(Severity.Error, $"it failed to compile ({compile.Groups[1].Value}: {compile.Groups[2].Value.Trim()})", [line]));
                continue;
            }

            if (Exception.IsMatch(line))
            {
                block = [line];
                blockKey = Owner(line, mods);
                continue;
            }

            if (Tagged.Match(line) is { Success: true } tagged)
            {
                var body = tagged.Groups[2].Value;
                var severity = ErrorLevel.IsMatch(body) ? Severity.Error
                    : WarningLevel.IsMatch(body) ? Severity.Warning
                    : ErrorWords.IsMatch(body) ? Severity.Error
                    : (Severity?)null;
                if (severity is { } level) Add(mods?.ForTag(tagged.Groups[1].Value) ?? Owner(line, mods), new LogProblem(level, Shorten(line), [line]));
                continue;
            }

            if (Trouble.IsMatch(line) || PathOwner(line) is not null && ErrorWords.IsMatch(line))
                Add(Owner(line, mods), new LogProblem(Severity.Error, Shorten(line), [line]));
        }

        Close();
        return new GameLog(version, byMod, unattributed);
    }

    static string? PathOwner(string line) =>
        SteamMod.Match(line) is { Success: true } steam ? ModFolder.SteamPrefix + steam.Groups[1].Value
        : LocalMod.Match(line) is { Success: true } local ? local.Groups[1].Value
        : null;

    static string? Owner(string line, ModFingerprints? mods)
    {
        if (PathOwner(line) is { } key) return key;
        if (mods is null) return null;
        if (FrameType.Match(line) is not { Success: true } frame) return null;
        return mods.ForType(frame.Groups[1].Success ? frame.Groups[1].Value : frame.Groups[2].Value);
    }

    static string Shorten(string line)
    {
        line = line.Trim();
        return line.Length <= 220 ? line : line[..220] + "…";
    }

    public IEnumerable<LogProblem> Errors(string key) =>
        ByMod.TryGetValue(key, out var problems) ? problems.Where(p => p.Severity == Severity.Error) : [];

    public string Report(IReadOnlyDictionary<string, string> names, DateTime played)
    {
        var text = new StringBuilder();
        text.AppendLine($"Flotilla game log report, UBOAT {GameVersion ?? "version unknown"}, game closed {played.ToLocalTime():yyyy-MM-dd HH:mm}");
        text.AppendLine();

        foreach (var (key, problems) in ByMod.OrderByDescending(m => m.Value.Count(p => p.Severity == Severity.Error)))
        {
            var errors = problems.Count(p => p.Severity == Severity.Error);
            text.AppendLine($"== {names.GetValueOrDefault(key, key)} ({key}): {errors} errors, {problems.Count - errors} warnings");
            foreach (var problem in problems)
            {
                text.AppendLine($"[{problem.Severity}] {problem.Summary}");
                foreach (var line in problem.Lines.Skip(1)) text.AppendLine($"    {line.Trim()}");
            }
            text.AppendLine();
        }

        if (Unattributed.Count > 0)
        {
            text.AppendLine($"== Not linked to a mod: {Unattributed.Count}");
            foreach (var problem in Unattributed)
            {
                text.AppendLine($"[{problem.Severity}] {problem.Summary}");
                foreach (var line in problem.Lines.Skip(1)) text.AppendLine($"    {line.Trim()}");
            }
        }
        return text.ToString();
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
