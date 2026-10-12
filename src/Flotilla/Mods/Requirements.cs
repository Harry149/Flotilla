using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flotilla.Mods;

public sealed record RequirementList(ulong[] Ids, DateTime Read);

public static class Requirements
{
    static readonly Regex ItemLink = new(@"filedetails/\?id=(\d+)", RegexOptions.Compiled);

    public static List<ulong> Parse(string html, ulong self = 0)
    {
        var start = html.IndexOf("id=\"RequiredItems\"", StringComparison.Ordinal);
        if (start < 0) return [];

        var end = html.IndexOf("<!--", start, StringComparison.Ordinal);
        var block = html[start..(end > start ? end : Math.Min(html.Length, start + 8000))];
        return ItemLink.Matches(block).Select(m => ulong.Parse(m.Groups[1].Value)).Where(id => id != self).Distinct().ToList();
    }

    public static Dictionary<ulong, RequirementList> Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<ulong, RequirementList>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return [];
        }
    }

    public static void Save(string path, Dictionary<ulong, RequirementList> lists)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(lists));
        File.Move(temporary, path, overwrite: true);
    }
}
