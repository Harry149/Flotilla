using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Flotilla.Steam;

public sealed class SteamCmd(string root)
{
    const string Bootstrap = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    static readonly Regex InstalledItem = new("\"(\\d+)\"\\s*\\{[^{}]*?\"timeupdated\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex InstalledManifest = new("\"(\\d+)\"\\s*\\{[^{}]*?\"manifest\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly SemaphoreSlim busy = new(1, 1);

    public string Executable { get; init; } = Path.Combine(root, "steamcmd.exe");
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromMinutes(5);

    string Content => Path.Combine(root, "steamapps", "workshop", "content", Workshop.AppId.ToString());
    string Staging => Path.Combine(root, "steamapps", "workshop", "downloads", Workshop.AppId.ToString());
    string Depot => Path.Combine(root, "steamapps", "content", $"app_{Workshop.AppId}", $"depot_{Workshop.AppId}");
    string Pinned => Path.Combine(root, "versions.txt");
    string Record => Path.Combine(root, "steamapps", "workshop", $"appworkshop_{Workshop.AppId}.acf");

    public async Task PrepareAsync(string mods, IProgress<string> status, CancellationToken cancel = default)
    {
        await busy.WaitAsync(cancel);
        try
        {
            await PrepareLockedAsync(mods, status, cancel);
        }
        finally
        {
            busy.Release();
        }
    }

    async Task PrepareLockedAsync(string mods, IProgress<string> status, CancellationToken cancel)
    {
        if (!File.Exists(Executable))
        {
            status.Report("Downloading SteamCMD");
            Directory.CreateDirectory(root);
            var zip = Path.Combine(root, "steamcmd.zip");

            using (var response = await Web.Client.GetAsync(Bootstrap, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file, cancel);
            }
            ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
            File.Delete(zip);

            status.Report("Setting up SteamCMD, this only happens once");
            await RunAsync(["+quit"], _ => { }, () => 0, cancel);
        }
        LinkContent(mods);
    }

    public async Task<Dictionary<ulong, string?>> DownloadAsync(IReadOnlyList<(ulong Id, long Size)> items, IProgress<SteamCmdEvent> progress, CancellationToken cancel = default)
    {
        await busy.WaitAsync(cancel);
        try
        {
            var results = await DownloadLockedAsync(items, progress, cancel);
            Unpin(results.Where(r => r.Value is null).Select(r => r.Key));
            return results;
        }
        finally
        {
            busy.Release();
        }
    }

    public async Task<string?> DownloadVersionAsync(ulong id, ulong manifest, long size, string target, IProgress<double> progress, CancellationToken cancel = default)
    {
        await busy.WaitAsync(cancel);
        try
        {
            if (Directory.Exists(Depot)) Directory.Delete(Depot, recursive: true);

            string? reason = "SteamCMD stopped before downloading it";
            void Read(string line)
            {
                if (SteamCmdOutput.ReadDepot(line) is { } update) reason = update.State == ItemState.Done ? null : update.Reason;
            }
            long Measure()
            {
                var now = FolderSize(Depot);
                if (size > 0) progress.Report(Math.Min(0.99, (double)now / size));
                return now;
            }

            string[] arguments = ["+login", "anonymous", "+download_depot", Workshop.AppId.ToString(), Workshop.AppId.ToString(), manifest.ToString(), "+quit"];
            if (!await RunAsync(arguments, Read, Measure, cancel)) return "SteamCMD stopped responding";
            if (reason is not null) return reason;
            if (!Directory.Exists(Depot)) return "SteamCMD finished without saving any files";

            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(Depot, target);
            Pin(id, manifest);
            return null;
        }
        finally
        {
            busy.Release();
        }
    }

    async Task<Dictionary<ulong, string?>> DownloadLockedAsync(IReadOnlyList<(ulong Id, long Size)> items, IProgress<SteamCmdEvent> progress, CancellationToken cancel)
    {
        var results = items.ToDictionary(i => i.Id, _ => (string?)"SteamCMD stopped before downloading it");
        var sizes = items.ToDictionary(i => i.Id, i => i.Size);
        var script = Path.Combine(root, "flotilla.txt");

        await File.WriteAllLinesAsync(script,
        [
            "@ShutdownOnFailedCommand 0",
            "@NoPromptForPassword 1",
            $"force_install_dir \"{root}\"",
            "login anonymous",
            .. items.Select(i => $"workshop_download_item {Workshop.AppId} {i.Id} validate"),
            "quit",
        ], cancel);

        ulong current = 0;
        long reported = -1;

        void Read(string line)
        {
            if (SteamCmdOutput.Read(line) is not { } update) return;

            if (update.Id == 0)
            {
                foreach (var id in results.Keys.ToList())
                {
                    if (results[id] is not null) results[id] = update.Reason;
                }
                return;
            }
            if (!results.ContainsKey(update.Id)) return;

            if (update.State == ItemState.Downloading)
            {
                current = update.Id;
                reported = -1;
            }
            else
            {
                results[update.Id] = update.State == ItemState.Done ? null : update.Reason;
            }
            progress.Report(update);
        }

        long Measure()
        {
            if (current == 0) return 0;
            var size = FolderSize(Path.Combine(Staging, current.ToString()));
            if (size != reported && sizes[current] > 0 && results[current] is not null)
            {
                reported = size;
                progress.Report(new SteamCmdEvent(current, ItemState.Downloading, Progress: Math.Min(0.99, (double)size / sizes[current])));
            }
            return size;
        }

        var finished = await RunAsync(["+runscript", script], Read, Measure, cancel);
        if (!finished)
        {
            foreach (var id in results.Keys.ToList())
            {
                if (results[id] is not null) results[id] = "SteamCMD stopped responding";
            }
        }
        return results;
    }

    public Dictionary<ulong, DateTime> InstalledVersions() => Versions(ReadText(Record));

    public Dictionary<ulong, ulong> InstalledManifests()
    {
        var manifests = Manifests(ReadText(Record));
        foreach (var (id, manifest) in ReadPins()) manifests[id] = manifest;
        return manifests;
    }

    public static Dictionary<ulong, DateTime> Versions(string acf) =>
        Installed(acf, InstalledItem).ToDictionary(i => i.Id, i => DateTimeOffset.FromUnixTimeSeconds((long)i.Value).UtcDateTime);

    public static Dictionary<ulong, ulong> Manifests(string acf) => Installed(acf, InstalledManifest).ToDictionary(i => i.Id, i => i.Value);

    static IEnumerable<(ulong Id, ulong Value)> Installed(string acf, Regex field)
    {
        var start = acf.IndexOf("\"WorkshopItemsInstalled\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return [];

        var end = acf.IndexOf("\"WorkshopItemDetails\"", start, StringComparison.OrdinalIgnoreCase);
        return field.Matches(end < 0 ? acf[start..] : acf[start..end])
            .Select(m => (Ok: ulong.TryParse(m.Groups[1].Value, out var id) & ulong.TryParse(m.Groups[2].Value, out var value), id, value))
            .Where(m => m.Ok)
            .DistinctBy(m => m.id)
            .Select(m => (m.id, m.value));
    }

    public static string ReadText(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    Dictionary<ulong, ulong> ReadPins()
    {
        var pins = new Dictionary<ulong, ulong>();
        foreach (var line in ReadText(Pinned).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && ulong.TryParse(parts[0], out var id) && ulong.TryParse(parts[1], out var manifest)) pins[id] = manifest;
        }
        return pins;
    }

    void Pin(ulong id, ulong manifest)
    {
        var pins = ReadPins();
        pins[id] = manifest;
        WritePins(pins);
    }

    void Unpin(IEnumerable<ulong> ids)
    {
        var pins = ReadPins();
        if (ids.Count(pins.Remove) > 0) WritePins(pins);
    }

    void WritePins(Dictionary<ulong, ulong> pins) =>
        File.WriteAllLines(Pinned, pins.Select(p => $"{p.Key} {p.Value}"));

    async Task<bool> RunAsync(string[] arguments, Action<string> read, Func<long> activity, CancellationToken cancel)
    {
        var log = new LogTail(Path.Combine(root, "logs", "console_log.txt"));
        var piped = new ConcurrentQueue<string>();
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) piped.Enqueue(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) piped.Enqueue(line); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        int Drain()
        {
            var count = 0;
            foreach (var line in log.ReadNew())
            {
                read(line);
                count++;
            }
            while (piped.TryDequeue(out var line))
            {
                read(line);
                count++;
            }
            return count;
        }

        var quietSince = DateTime.UtcNow;
        var lastActivity = -1L;
        var finished = true;

        while (!process.HasExited)
        {
            var lines = Drain();
            var now = activity();
            if (lines > 0 || now != lastActivity)
            {
                quietSince = DateTime.UtcNow;
                lastActivity = now;
            }
            if (cancel.IsCancellationRequested || DateTime.UtcNow - quietSince > StallAfter)
            {
                process.Kill(entireProcessTree: true);
                finished = false;
                break;
            }
            await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        Drain();
        cancel.ThrowIfCancellationRequested();
        return finished;
    }

    void LinkContent(string mods)
    {
        Directory.CreateDirectory(mods);
        var content = new DirectoryInfo(Content);
        if (content.Exists && content.LinkTarget is not null) return;

        if (content.Exists)
        {
            foreach (var folder in content.GetDirectories())
            {
                var target = Path.Combine(mods, folder.Name);
                if (Directory.Exists(target)) folder.Delete(recursive: true);
                else folder.MoveTo(target);
            }
            content.Delete(recursive: true);
        }
        Directory.CreateDirectory(content.Parent!.FullName);

        var link = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", content.FullName, mods }) link.ArgumentList.Add(argument);
        using (var process = Process.Start(link)!) process.WaitForExit();

        if (!Directory.Exists(content.FullName))
            throw new IOException($"Couldn't connect SteamCMD's download folder to {mods}.");
    }

    static long FolderSize(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
