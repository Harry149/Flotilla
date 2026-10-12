using System.IO;
using System.Text.Json;

namespace Flotilla.Mods;

public sealed record Profile(string Name, IReadOnlyList<string> Mods);

public static class ProfileStore
{
    static readonly StringComparer Keys = StringComparer.OrdinalIgnoreCase;
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static List<Profile> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];

            return (JsonSerializer.Deserialize<List<Profile?>>(File.ReadAllText(path)) ?? [])
                .Where(p => p is { Name.Length: > 0 })
                .Select(p => p! with { Mods = [.. (p.Mods ?? []).Where(k => !string.IsNullOrWhiteSpace(k))] })
                .DistinctBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return [];
        }
    }

    public static void Save(string path, IEnumerable<Profile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, Indented));
        File.Move(temporary, path, overwrite: true);
    }

    public static (List<string> On, List<string> Missing) Resolve(IEnumerable<string> available, IEnumerable<string> wanted)
    {
        var present = new HashSet<string>(available, Keys);
        var on = new List<string>();
        var missing = new List<string>();

        foreach (var key in wanted)
        {
            var match = Alternatives(key).Select(k => present.TryGetValue(k, out var actual) ? actual : null).FirstOrDefault(k => k is not null);
            if (match is null) missing.Add(key);
            else if (!on.Contains(match, Keys)) on.Add(match);
        }
        return (on, missing);
    }

    public static bool IsActive(Profile profile, IEnumerable<string> available, IEnumerable<string> enabled)
    {
        var (on, missing) = Resolve(available, profile.Mods);
        return missing.Count == 0 && on.SequenceEqual(enabled, Keys);
    }

    static IEnumerable<string> Alternatives(string key)
    {
        yield return key;
        if (key.StartsWith(ModFolder.SteamPrefix, StringComparison.OrdinalIgnoreCase)) yield return key[ModFolder.SteamPrefix.Length..];
        else if (ulong.TryParse(key, out _)) yield return ModFolder.SteamPrefix + key;
    }
}
