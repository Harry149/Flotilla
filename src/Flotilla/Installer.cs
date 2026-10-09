using Flotilla.Steam;
using Flotilla.UI;

namespace Flotilla;

public sealed class Installer(SteamCmd steam, string mods, StatusLine status)
{
    const int BatchSize = 25;
    const int Attempts = 3;

    readonly List<ModTile> queue = [];
    bool running;

    public event Action<ModTile>? Installed;

    public void Add(ModTile tile)
    {
        if (queue.Contains(tile)) return;

        tile.Queue();
        queue.Add(tile);
        if (!running) _ = RunAsync();
    }

    async Task RunAsync()
    {
        running = true;
        try
        {
            await steam.PrepareAsync(mods, new Progress<string>(text => status.Work("Setting up SteamCMD", text)));
            while (queue.Count > 0)
            {
                var batch = queue.Take(BatchSize).ToList();
                await InstallAsync(batch);
                queue.RemoveAll(batch.Contains);
            }
        }
        catch (Exception e)
        {
            foreach (var tile in queue) tile.Fail(e.Message);
            queue.Clear();
            status.Warn($"SteamCMD couldn't run: {e.Message}");
        }
        finally
        {
            running = false;
            status.Idle();
        }
    }

    async Task InstallAsync(List<ModTile> batch)
    {
        var pending = batch;
        var reasons = new Dictionary<ulong, string>();
        var done = 0;

        for (var attempt = 1; attempt <= Attempts && pending.Count > 0; attempt++)
        {
            var progress = new Progress<SteamCmdEvent>(update =>
            {
                if (update.State != ItemState.Downloading || pending.Find(t => t.Id == update.Id) is not { } tile) return;

                tile.Downloading(update.Progress);
                var detail = batch.Count > 1 ? $"Installing {done + 1} of {batch.Count}" : update.Progress > 0 ? $"Installing, {update.Progress * 100:0}%" : "Installing";
                status.Work(tile.Title, detail, (done + update.Progress) / batch.Count, tile);
            });

            var results = await steam.DownloadAsync([.. pending.Select(t => (t.Id, t.Item.Size))], progress);

            foreach (var tile in pending.Where(t => results[t.Id] is null))
            {
                done++;
                tile.Finish();
                Installed?.Invoke(tile);
            }
            foreach (var tile in pending)
            {
                if (results[tile.Id] is { } reason) reasons[tile.Id] = reason;
            }
            pending = [.. pending.Where(t => results[t.Id] is not null)];
        }

        foreach (var tile in pending) tile.Fail(reasons[tile.Id]);

        if (pending.Count == 1) status.Warn($"{pending[0].Title} didn't install: {reasons[pending[0].Id]}.");
        else if (pending.Count > 1) status.Warn($"{pending.Count} mods didn't install. Open one to see why.");
    }
}
