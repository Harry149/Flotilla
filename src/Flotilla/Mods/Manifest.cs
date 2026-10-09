using System.IO;
using System.Text.Json;

namespace Flotilla.Mods;

public sealed record Manifest(string Name, string Version, IReadOnlyList<string> GameVersions, ulong SteamId)
{
    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static Manifest? Read(string folder)
    {
        var path = Path.Combine(folder, "Manifest.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), Lenient);
            var root = document.RootElement;
            return new Manifest(
                root.Text("name").Trim(),
                root.Text("version").Trim(),
                GameVersionsOf(root),
                (ulong)Math.Max(0, root.Number("steamFileId")));
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    static List<string> GameVersionsOf(JsonElement root)
    {
        IEnumerable<string> versions = root.TryGet("supportedGameVersions", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            : [root.Text("minGameVersion"), root.Text("maxGameVersion")];

        return versions
            .Select(v => v.Split('\n')[0].Trim())
            .Where(v => v.Length > 0)
            .Distinct()
            .ToList();
    }
}
