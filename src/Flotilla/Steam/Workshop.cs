using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flotilla.Steam;

public sealed class Workshop(HttpClient http)
{
    public const uint AppId = 494840;

    const string BrowsePage = "https://steamcommunity.com/workshop/browse/?appid=494840&browsesort=mostrecent&section=readytouseitems&actualsort=mostrecent&p=";
    const string DetailsApi = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    const int PagesAtOnce = 4;

    static readonly Regex ItemLink = new(@"sharedfiles/filedetails/\?id=(\d+)", RegexOptions.Compiled);

    public async Task<List<ulong>> ListAsync(CancellationToken cancel = default)
    {
        var ids = new List<ulong>();
        var seen = new HashSet<ulong>();

        for (var page = 1; page < 500; page += PagesAtOnce)
        {
            var pages = await Task.WhenAll(Enumerable.Range(page, PagesAtOnce)
                .Select(p => Retry(() => http.GetStringAsync(BrowsePage + p, cancel), cancel)));

            var before = ids.Count;
            foreach (var id in pages.SelectMany(ParseIds))
            {
                if (seen.Add(id)) ids.Add(id);
            }
            if (ids.Count == before) break;
        }
        return ids;
    }

    public async Task<List<WorkshopItem>> DetailsAsync(IEnumerable<ulong> ids, CancellationToken cancel = default)
    {
        var items = new List<WorkshopItem>();

        foreach (var batch in ids.Chunk(100))
        {
            var form = new Dictionary<string, string> { ["itemcount"] = batch.Length.ToString() };
            for (var i = 0; i < batch.Length; i++) form[$"publishedfileids[{i}]"] = batch[i].ToString();

            var json = await Retry(async () =>
            {
                using var response = await http.PostAsync(DetailsApi, new FormUrlEncodedContent(form), cancel);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancel);
            }, cancel);

            items.AddRange(ParseDetails(json));
        }
        return items;
    }

    public static IEnumerable<ulong> ParseIds(string html) =>
        ItemLink.Matches(html).Select(m => ulong.Parse(m.Groups[1].Value));

    public static List<WorkshopItem> ParseDetails(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = new List<WorkshopItem>();

        if (!document.RootElement.TryGet("response", out var response) || !response.TryGet("publishedfiledetails", out var details))
            return items;

        foreach (var item in details.EnumerateArray())
        {
            if (item.Number("result") != 1 || item.Number("consumer_app_id") != AppId || item.Number("banned") != 0) continue;

            string[] tags = item.TryGet("tags", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(t => t.Text("tag")).Where(t => t.Length > 0)]
                : [];

            items.Add(new WorkshopItem(
                (ulong)item.Number("publishedfileid"),
                item.Text("title").Trim(),
                item.Text("description"),
                item.Text("preview_url"),
                item.Number("file_size"),
                Time(item.Number("time_created")),
                Time(item.Number("time_updated")),
                (int)item.Number("subscriptions"),
                tags,
                ulong.TryParse(item.Text("hcontent_file"), out var manifest) ? manifest : 0));
        }
        return items;
    }

    public static List<WorkshopItem> Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<List<WorkshopItem>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    public static void Save(string file, IEnumerable<WorkshopItem> items) =>
        File.WriteAllText(file, JsonSerializer.Serialize(items));

    static DateTime Time(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    static async Task<string> Retry(Func<Task<string>> request, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await request();
            }
            catch (HttpRequestException e) when (attempt < 3 && e.StatusCode is null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancel);
            }
        }
    }
}
