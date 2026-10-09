using System.Net;
using System.Net.Http;

namespace Flotilla;

public sealed class Featured(HttpClient http)
{
    const string Url = "https://api.github.com/repos/Harry149/Flotilla/contents/featured.txt";

    string? etag;

    public async Task<List<ulong>?> ReadAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Accept.ParseAdd("application/vnd.github.raw");
        if (etag is not null) request.Headers.IfNoneMatch.ParseAdd(etag);

        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        etag = response.Headers.ETag?.ToString();
        return Parse(await response.Content.ReadAsStringAsync());
    }

    public static List<ulong> Parse(string text) =>
        text.Split('\n')
            .Select(line => line.Split('#')[0].Trim())
            .Select(line => ulong.TryParse(line, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
}
