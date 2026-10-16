using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flotilla;

public sealed record VoteCount(int Works, int Broken)
{
    public int Total => Works + Broken;
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Works / Total);
}

public sealed record SharedCode(string Code, DateTime? Expires);

public sealed class Community(HttpClient http)
{
    static string Api => Sailors.Api;

    public async Task<SharedCode?> ShareAsync(string installId, string setup, int hours)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/share", new { id = installId, body = setup, hours });
        return response.IsSuccessStatusCode ? ParseShare(await response.Content.ReadAsStringAsync()) : null;
    }

    public static SharedCode? ParseShare(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.Text("code") is not { Length: > 0 } code) return null;

        // An older server sends no expiry, so none is shown.
        var expires = document.RootElement.Number("expires");
        return new SharedCode(code, expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime : null);
    }

    public async Task<string?> ReadShareAsync(string code)
    {
        using var response = await http.GetAsync($"{Api}/share/{Uri.EscapeDataString(code)}");
        if (!response.IsSuccessStatusCode) return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Text("body") is { Length: > 0 } body ? body : null;
    }

    public async Task<bool> VoteAsync(string installId, ulong mod, string game, bool? works)
    {
        using var response = await http.PostAsJsonAsync($"{Api}/vote", new { id = installId, mod = mod.ToString(), game, works });
        return response.IsSuccessStatusCode;
    }

    public async Task<Dictionary<ulong, VoteCount>> VotesAsync(string game)
    {
        using var response = await http.GetAsync($"{Api}/votes?game={Uri.EscapeDataString(game)}");
        return response.IsSuccessStatusCode ? ParseVotes(await response.Content.ReadAsStringAsync()) : [];
    }

    public static Dictionary<ulong, VoteCount> ParseVotes(string json)
    {
        using var document = JsonDocument.Parse(json);
        var votes = new Dictionary<ulong, VoteCount>();
        if (!document.RootElement.TryGet("mods", out var mods) || mods.ValueKind != JsonValueKind.Object) return votes;

        foreach (var mod in mods.EnumerateObject())
        {
            if (!ulong.TryParse(mod.Name, out var id) || mod.Value.ValueKind != JsonValueKind.Array || mod.Value.GetArrayLength() != 2) continue;
            votes[id] = new VoteCount(mod.Value[0].GetInt32(), mod.Value[1].GetInt32());
        }
        return votes;
    }
}
