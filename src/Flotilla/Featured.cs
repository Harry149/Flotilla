using System.Net;
using System.Net.Http;

namespace Flotilla;

// Reads featured.txt from the GitHub repo. GitHub's API answers within seconds of a push (raw.githubusercontent.com
// caches for minutes), and a conditional request that comes back unchanged doesn't count against its hourly limit.
public sealed class Featured(HttpClient http)
{
    const string Url = "https://api.github.com/repos/Harry149/Flotilla/contents/featured.txt";

    string? etag;

    // The Workshop IDs to feature, or null when the file hasn't changed since the last read.
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

    // One Workshop ID per line. Anything after a # is a note, and lines that aren't IDs are skipped.
    public static List<ulong> Parse(string text) =>
        text.Split('\n')
            .Select(line => line.Split('#')[0].Trim())
            .Select(line => ulong.TryParse(line, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
}
