using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Flotilla.Mods;

public enum FieldKind { Switch, Number, Text }

public sealed class SettingField(string key, string value, FieldKind kind, string description)
{
    public string Key { get; } = key;
    public string Label { get; } = Words(key);
    public string Description { get; } = description;
    public FieldKind Kind { get; } = kind;
    public string Original { get; } = value;
    public string Value { get; set; } = value;
    public bool IsOn
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = Original.Length > 0 && char.IsUpper(Original[0]) ? (value ? "True" : "False") : (value ? "true" : "false");
    }
    public bool IsSwitch => Kind == FieldKind.Switch;
    public bool Changed => Value != Original;

    static string Words(string key)
    {
        var spaced = Regex.Replace(key.Replace('_', ' ').Replace('-', ' '), "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
        return spaced.Length == 0 ? key : char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }
}

public sealed class SettingsFile
{
    static readonly Regex Assignment = new(@"^\s*([A-Za-z_][\w.-]*)\s*[=:]\s*(.*?)\s*$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    SettingsFile(string path, string about, List<SettingField> fields, bool json)
    {
        Path = path;
        About = about;
        Fields = fields;
        IsJson = json;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string About { get; }
    public IReadOnlyList<SettingField> Fields { get; }
    public bool IsJson { get; }

    public static SettingsFile? Read(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? FromJson(path, text) : FromLines(path, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static SettingsFile? FromJson(string path, string text)
    {
        if (JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root) return null;

        var fields = new List<SettingField>();
        foreach (var (key, node) in root)
        {
            if (node is not JsonValue value) continue;
            var kind = value.GetValueKind();
            fields.Add(kind switch
            {
                JsonValueKind.True or JsonValueKind.False => new SettingField(key, kind == JsonValueKind.True ? "true" : "false", FieldKind.Switch, ""),
                JsonValueKind.Number => new SettingField(key, value.ToJsonString(), FieldKind.Number, ""),
                JsonValueKind.String => new SettingField(key, value.GetValue<string>(), FieldKind.Text, ""),
                _ => new SettingField(key, value.ToJsonString(), FieldKind.Text, ""),
            });
        }
        return fields.Count == 0 ? null : new SettingsFile(path, "", fields, json: true);
    }

    public static SettingsFile? FromLines(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fields = new List<SettingField>();
        var comment = new List<string>();
        var about = new List<string>();
        var heading = true;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//") || trimmed.StartsWith(';'))
            {
                comment.Add(trimmed.TrimStart('#', '/', ';').Trim());
                continue;
            }
            if (trimmed.Length == 0)
            {
                if (heading && comment.Count > 0) about.AddRange(comment);
                heading = false;
                comment.Clear();
                continue;
            }
            if (Assignment.Match(line) is not { Success: true } match)
            {
                comment.Clear();
                continue;
            }

            heading = false;
            var value = match.Groups[2].Value;
            var kind = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) ? FieldKind.Switch
                : double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? FieldKind.Number
                : FieldKind.Text;
            fields.Add(new SettingField(match.Groups[1].Value, value, kind, string.Join(" ", comment)));
            comment.Clear();
        }
        return fields.Count == 0 ? null : new SettingsFile(path, string.Join(" ", about), fields, json: false);
    }

    public string? Problem(SettingField field) =>
        field.Kind == FieldKind.Number && !double.TryParse(field.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? $"{field.Label} needs a number, like {field.Original}."
            : null;

    public string Render(string original)
    {
        var changed = Fields.Where(f => f.Changed).ToDictionary(f => f.Key);
        if (changed.Count == 0) return original;

        if (IsJson)
        {
            var root = JsonNode.Parse(original, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
            foreach (var field in changed.Values)
            {
                root[field.Key] = field.Kind switch
                {
                    FieldKind.Switch => JsonValue.Create(field.IsOn),
                    FieldKind.Number => JsonNode.Parse(field.Value.Trim()),
                    _ => JsonValue.Create(field.Value),
                };
            }
            return root.ToJsonString(Indented);
        }

        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var output = new StringBuilder();
        var lines = original.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Assignment.Match(line) is { Success: true } match && changed.TryGetValue(match.Groups[1].Value, out var field))
            {
                var at = match.Groups[2].Index;
                line = line[..at] + field.Value.Trim() + line[(at + match.Groups[2].Length)..];
            }
            output.Append(line);
            if (i < lines.Length - 1) output.Append(newline);
        }
        return output.ToString();
    }

    public void Save()
    {
        var original = File.ReadAllText(Path);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, Render(original), new UTF8Encoding(false));
        File.Move(temporary, Path, overwrite: true);
    }

    public static Dictionary<string, List<string>> Find(string game, IEnumerable<(string Key, string? Folder)> mods)
    {
        var candidates = Candidates(game).ToList();
        var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0) return owners;

        foreach (var (key, folder) in mods)
        {
            if (folder is null || !Directory.Exists(folder)) continue;

            var source = new StringBuilder();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Take(400)) source.Append(File.ReadAllText(file));
                foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories).Take(50)) source.Append(' ').Append(System.IO.Path.GetFileNameWithoutExtension(dll)).Append(' ');
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            var text = source.ToString();
            foreach (var candidate in candidates)
            {
                var name = System.IO.Path.GetFileName(candidate);
                var directory = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(candidate)) ?? "";
                var inModData = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(candidate))) == "ModData";
                if (!text.Contains(name, StringComparison.OrdinalIgnoreCase) && !(inModData && text.Contains(directory, StringComparison.OrdinalIgnoreCase))) continue;

                if (!owners.TryGetValue(key, out var files)) owners[key] = files = [];
                files.Add(candidate);
            }
        }

        var claimed = owners.SelectMany(o => o.Value).GroupBy(f => f, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var files in owners.Values) files.RemoveAll(claimed.Contains);
        foreach (var empty in owners.Where(o => o.Value.Count == 0).Select(o => o.Key).ToList()) owners.Remove(empty);
        return owners;
    }

    static IEnumerable<string> Candidates(string game)
    {
        string[] kinds = [".txt", ".json", ".ini", ".cfg"];
        bool Wanted(string path) => kinds.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            && !System.IO.Path.GetFileName(path).StartsWith("output_log", StringComparison.OrdinalIgnoreCase)
            && !System.IO.Path.GetFileName(path).StartsWith("modlist", StringComparison.OrdinalIgnoreCase);

        if (Directory.Exists(game))
            foreach (var file in Directory.EnumerateFiles(game).Where(Wanted)) yield return file;

        var data = System.IO.Path.Combine(game, "ModData");
        if (!Directory.Exists(data)) yield break;
        foreach (var folder in Directory.EnumerateDirectories(data))
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => Wanted(f) && IsSettingsName(System.IO.Path.GetFileName(f))))
                yield return file;
    }

    static bool IsSettingsName(string name) =>
        name.Contains("setting", StringComparison.OrdinalIgnoreCase) || name.Contains("config", StringComparison.OrdinalIgnoreCase);
}
