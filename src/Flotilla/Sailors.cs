using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flotilla;

public sealed class Sailors(HttpClient http)
{
    public const string Api = "https://flotilla-api.tyrrh019-851.workers.dev";
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(4);

    public static bool Enabled => Api.Length > 0;

    public Task<long?> PingAsync(string installId) => PostAsync("ping", installId);

    public Task<long?> ByeAsync(string installId) => PostAsync("bye", installId);

    public async Task<long?> CountAsync()
    {
        using var response = await http.GetAsync($"{Api}/stats");
        return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync()) : null;
    }

    async Task<long?> PostAsync(string action, string installId)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/{action}", new { id = installId, version = Updater.Current });
        return response.IsSuccessStatusCode ? Parse(await response.Content.ReadAsStringAsync()) : null;
    }

    public static long? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGet("sailors", out var value) && value.TryGetInt64(out var count) ? count : null;
    }
}
