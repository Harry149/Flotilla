using System.IO;
using System.Text.RegularExpressions;

namespace Flotilla.Mods;

public sealed class ModFingerprints
{
    const int MaxFiles = 400;

    static readonly Regex Namespace = new(@"^\s*namespace\s+([A-Za-z_][\w.]*)", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex LogTag = new(@"""\[([A-Za-z][\w .-]{1,30})\]", RegexOptions.Compiled);
    static readonly Regex Release = new(@"^\s*\[[^\]]*\]\s*", RegexOptions.Compiled);
    static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "UnityEngine", "HarmonyLib", "Harmony", "UBOAT", "DWS", "Mono", "Microsoft", "Newtonsoft",
    };

    readonly Dictionary<string, string> namespaces;
    readonly Dictionary<string, string> tags;

    public ModFingerprints(Dictionary<string, string> namespaces, Dictionary<string, string> tags)
    {
        this.namespaces = new(namespaces.Where(n => !Shared.Contains(n.Key)), StringComparer.Ordinal);
        this.tags = new(tags.Select(t => KeyValuePair.Create(Squash(t.Key), t.Value)).Where(t => t.Key.Length > 1)
            .DistinctBy(t => t.Key), StringComparer.Ordinal);
    }

    public static ModFingerprints From(IEnumerable<(string Key, string Name, string? Folder)> mods)
    {
        var namespaces = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var tags = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Note(Dictionary<string, HashSet<string>> map, string value, string key)
        {
            if (!map.TryGetValue(value, out var keys)) map[value] = keys = new(StringComparer.OrdinalIgnoreCase);
            keys.Add(key);
        }

        foreach (var (key, name, folder) in mods)
        {
            Note(tags, Squash(Release.Replace(name, "")), key);
            if (folder is null || !Directory.Exists(folder)) continue;
            if (Manifest.Read(folder)?.AssemblyName is { Length: > 0 } assembly) Note(namespaces, assembly, key);

            try
            {
                foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Take(MaxFiles))
                    Note(namespaces, Path.GetFileNameWithoutExtension(dll), key);

                foreach (var source in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Take(MaxFiles))
                {
                    var text = File.ReadAllText(source);
                    foreach (Match match in Namespace.Matches(text)) Note(namespaces, match.Groups[1].Value, key);
                    foreach (Match match in LogTag.Matches(text)) Note(tags, Squash(match.Groups[1].Value), key);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        static Dictionary<string, string> Unique(Dictionary<string, HashSet<string>> map) =>
            map.Where(m => m.Value.Count == 1).ToDictionary(m => m.Key, m => m.Value.First(), StringComparer.Ordinal);

        return new ModFingerprints(Unique(namespaces), Unique(tags));
    }

    public string? ForType(string typeName)
    {
        string? best = null;
        var length = 0;
        foreach (var (prefix, key) in namespaces)
        {
            if (prefix.Length <= length) continue;
            if (typeName == prefix || typeName.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                best = key;
                length = prefix.Length;
            }
        }
        return best;
    }

    public string? ForTag(string tag) => tags.GetValueOrDefault(Squash(tag));

    // The mod whose DLL, or the assembly the game compiles its source into, has exactly this name.
    public string? ForAssembly(string name) => namespaces.GetValueOrDefault(name);

    static string Squash(string text) => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
