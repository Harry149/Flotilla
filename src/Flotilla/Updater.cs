using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flotilla;

public sealed record Update(string Version, string Notes, string Page, string Download, long Size, string? Sha256);

public sealed class Updater(HttpClient http)
{
    string? etag;
    string? latest;

    const string LatestRelease = "https://api.github.com/repos/Harry149/Flotilla/releases/latest";
    const string Releases = "https://api.github.com/repos/Harry149/Flotilla/releases?per_page=100";
    const string AssetName = "Flotilla.exe";
    public const string Updated = "--updated";

    static readonly Regex Number = new(@"(\d+)\.(\d+)(?:\.(\d+))?([a-z])?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Current
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    public static bool CanReplaceItself => Environment.ProcessPath is { } exe && typeof(Updater).Assembly.Location is var code
        && (code.Length == 0 || !string.Equals(Path.GetDirectoryName(code), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase));

    public async Task<Update?> CheckAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request);
        if (response.StatusCode != System.Net.HttpStatusCode.NotModified)
        {
            response.EnsureSuccessStatusCode();
            latest = await response.Content.ReadAsStringAsync();
            etag = response.Headers.ETag?.ToString();
        }
        if (latest is null) return null;

        var update = Parse(latest);
        if (update is null || !IsNewer(update.Version, Current)) return null;
        return update.Sha256 is { } hash && await IsRunningAsync(hash) ? null : update;
    }

    static async Task<bool> IsRunningAsync(string sha256)
    {
        if (!CanReplaceItself) return false;
        try
        {
            await using var file = new FileStream(Environment.ProcessPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<long?> DownloadsAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Releases);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request);
        return response.IsSuccessStatusCode ? CountDownloads(await response.Content.ReadAsStringAsync()) : null;
    }

    public static long CountDownloads(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return 0;

        return document.RootElement.EnumerateArray()
            .Where(release => release.TryGet("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            .SelectMany(release => release.GetProperty("assets").EnumerateArray())
            .Where(asset => asset.Text("name").Equals(AssetName, StringComparison.OrdinalIgnoreCase))
            .Sum(asset => asset.Number("download_count"));
    }

    public static Update? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.Number("draft") != 0 || root.Number("prerelease") != 0) return null;

        var version = new[] { root.Text("name"), root.Text("tag_name") }.Select(VersionIn).FirstOrDefault(v => v is not null);
        if (version is null || !root.TryGet("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.Text("name").Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            var digest = asset.Text("digest");
            return new Update(
                version,
                root.Text("body").Trim(),
                root.Text("html_url"),
                asset.Text("browser_download_url"),
                asset.Number("size"),
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return null;
    }

    public static string? VersionIn(string text) => Number.Match(text) is { Success: true } match ? match.Value.ToLowerInvariant() : null;

    public static bool IsNewer(string release, string current) => Compare(release, current) > 0;

    static int Compare(string a, string b)
    {
        var (x, y) = (Parts(a), Parts(b));
        for (var i = 0; i < x.Length; i++)
        {
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        }
        return 0;
    }

    static int[] Parts(string version)
    {
        if (Number.Match(version) is not { Success: true } match) return [0, 0, 0, 0];

        int Part(int group) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value) : 0;
        var letter = match.Groups[4].Success ? char.ToLowerInvariant(match.Groups[4].Value[0]) - 'a' + 1 : 0;
        return [Part(1), Part(2), Part(3), letter];
    }

    public static string Pending => Environment.ProcessPath! + ".new";

    public async Task<string> DownloadAsync(Update update, IProgress<double> progress, CancellationToken cancel = default)
    {
        var target = Pending;
        if (await IsDownloadedAsync(target, update, cancel)) return target;

        using var response = await http.GetAsync(update.Download, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? update.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        if (update.Size > 0 && new FileInfo(target).Length != update.Size) throw Damaged(target, "it's the wrong size");
        if (update.Sha256 is { } expected)
        {
            await using var file = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw Damaged(target, "its checksum doesn't match");
        }
        return target;
    }

    static async Task<bool> IsDownloadedAsync(string path, Update update, CancellationToken cancel)
    {
        if (update.Sha256 is not { } expected || !File.Exists(path) || (update.Size > 0 && new FileInfo(path).Length != update.Size)) return false;
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancel)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    static IOException Damaged(string path, string reason)
    {
        File.Delete(path);
        return new IOException($"The download was damaged ({reason}). Try again later.");
    }

    public static Process? Install(string downloaded, string arguments = "")
    {
        var current = Environment.ProcessPath!;
        var old = current + ".old";
        File.Delete(old);
        File.Move(current, old);
        try
        {
            File.Move(downloaded, current);
        }
        catch
        {
            File.Move(old, current);
            throw;
        }
        return Process.Start(new ProcessStartInfo(current, arguments) { UseShellExecute = true });
    }

    public static void CleanUp(bool download = false)
    {
        if (Environment.ProcessPath is not { } path) return;
        try
        {
            var leftover = path + (download ? ".new" : ".old");
            if (File.Exists(leftover)) File.Delete(leftover);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
