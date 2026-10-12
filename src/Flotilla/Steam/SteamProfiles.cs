using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Flotilla.Steam;

public sealed class SteamProfiles(HttpClient http)
{
    static readonly Regex Name = new(@"<steamID>\s*(?:<!\[CDATA\[(.*?)\]\]>|([^<]*))\s*</steamID>", RegexOptions.Compiled | RegexOptions.Singleline);

    readonly Dictionary<ulong, Task<string?>> names = [];

    public Task<string?> NameAsync(ulong id)
    {
        if (!names.TryGetValue(id, out var name)) names[id] = name = FetchAsync(id);
        return name;
    }

    async Task<string?> FetchAsync(ulong id)
    {
        try
        {
            var name = ParseName(await http.GetStringAsync($"https://steamcommunity.com/profiles/{id}/?xml=1"));
            if (name is null) names.Remove(id);
            return name;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            names.Remove(id);
            return null;
        }
    }

    public static string? ParseName(string xml)
    {
        var match = Name.Match(xml);
        if (!match.Success) return null;

        var name = (match.Groups[1].Success ? match.Groups[1].Value : WebUtility.HtmlDecode(match.Groups[2].Value)).Trim();
        return name.Length > 0 ? name : null;
    }
}
