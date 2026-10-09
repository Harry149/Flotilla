using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Flotilla.Mods;
using Flotilla.Steam;
using Flotilla.UI;

namespace Flotilla;

public sealed class Library : Observable
{
    readonly Workshop workshop = new(Web.Client);
    readonly ModList modList = new(Paths.ModList, Path.Combine(Paths.AppData, "order.txt"));
    readonly string catalogue = Path.Combine(Paths.AppData, "workshop.json");
    readonly SteamCmd steam = new(Path.Combine(Paths.AppData, "steamcmd"));
    readonly Installer installer;
    Dictionary<ulong, ModTile> tiles = [];

    public Library()
    {
        installer = new Installer(steam, Paths.Mods, Status);
        installer.Installed += OnInstalled;
    }

    public StatusLine Status { get; } = new();
    public ObservableCollection<InstalledMod> Installed { get; } = [];
    public IReadOnlyList<ModTile> Tiles { get; private set => Set(ref field, value); } = [];
    public IReadOnlyList<ulong> FeaturedIds { get; private set => Set(ref field, value); } = [];
    public string? WorkshopError { get; private set => Set(ref field, value); }
    public string InstalledSummary { get; private set => Set(ref field, value); } = "";
    public int UpdateCount { get; private set => Set(ref field, value); }
    public bool Importing
    {
        get;
        private set
        {
            if (Set(ref field, value)) Raise(nameof(CanImport));
        }
    }
    public bool CanImport => !Importing;

    public async Task StartAsync()
    {
        Show(Workshop.Load(catalogue));
        Reload();
        _ = WatchFeaturedAsync();
        await RefreshWorkshopAsync();

        if (UpdateCount > 0)
            Status.Say(UpdateCount == 1 ? "1 mod has an update. Update it from Installed." : $"{UpdateCount} mods have updates. Update them from Installed.");
    }

    public async Task RefreshWorkshopAsync()
    {
        WorkshopError = null;
        try
        {
            var items = await workshop.DetailsAsync(await workshop.ListAsync());
            Workshop.Save(catalogue, items);
            Show(items);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            WorkshopError = e.Message;
            Status.Warn($"Couldn't read the Steam Workshop: {e.Message}");
        }
    }

    async Task WatchFeaturedAsync()
    {
        var featured = new Featured(Web.Client);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                if (await featured.ReadAsync() is { } ids && !ids.SequenceEqual(FeaturedIds)) FeaturedIds = ids;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Offline or rate limited: keep what's showing and try again next minute.
            }
        } while (await timer.WaitForNextTickAsync());
    }

    public void Install(ModTile tile)
    {
        if (tile.CanInstall) installer.Add(tile);
        CountUpdates();
    }

    public void UpdateAll()
    {
        foreach (var tile in Tiles.Where(t => t.State == TileState.Outdated).ToList()) installer.Add(tile);
        CountUpdates();
    }

    public void Move(InstalledMod mod, int index)
    {
        var from = Installed.IndexOf(mod);
        index = Math.Clamp(index, 0, Installed.Count - 1);
        if (from < 0 || from == index) return;

        Installed.Move(from, index);
        Save();
    }

    public bool Save()
    {
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. Mod changes can't be saved while the game or its launcher is open.");
            Reload();
            return false;
        }

        try
        {
            modList.Save(Installed.Select(m => (m.Key, m.Enabled)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Warn($"Couldn't save UBOAT's mod list: {e.Message}");
            Reload();
            return false;
        }
        Renumber();
        return true;
    }

    public void Remove(InstalledMod mod)
    {
        if (mod.Source == ModSource.Steam)
        {
            if (mod.WorkshopId is { } id) Shell.OpenInSteam(id);
            Status.Say($"Unsubscribe from {mod.Name} in Steam and Steam will delete it.");
            return;
        }
        if (mod.Source != ModSource.Missing)
        {
            if (GameIsRunning())
            {
                Status.Warn($"Close UBOAT first. {mod.Name} can't be removed while the game is open.");
                return;
            }
            try
            {
                Directory.Delete(mod.Folder.Path!, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Status.Warn($"Couldn't delete {mod.Name}: {e.Message}");
                return;
            }
        }

        Installed.Remove(mod);
        if (Save()) Status.Say(mod.Source == ModSource.Missing ? $"Removed {mod.Name} from the list" : $"Uninstalled {mod.Name}");
        SyncTiles();
    }

    public void Export(string path)
    {
        var subscribed = SteamLibrary.Manifests();
        var downloaded = steam.InstalledManifests();
        var mods = Installed.Where(m => m.Enabled && m.Source != ModSource.Missing).Select(m => m switch
        {
            { Source: ModSource.Steam, WorkshopId: { } id } => new PackEntry(id.ToString(), subscribed.GetValueOrDefault(id), m.Name),
            { Source: ModSource.Download, WorkshopId: { } id } => new PackEntry(id.ToString(), downloaded.GetValueOrDefault(id), m.Name),
            _ => new PackEntry(m.Key, 0, m.Name),
        }).ToList();

        if (mods.Count == 0)
        {
            Status.Warn("No mods are switched on, so there's nothing to export.");
            return;
        }
        try
        {
            File.WriteAllText(path, ModPack.Write(mods));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Warn($"Couldn't save the mod list: {e.Message}");
            return;
        }
        Status.Say(mods.Count == 1 ? "Exported 1 mod." : $"Exported {mods.Count} mods, in load order.");
    }

    // Installs every mod in an exported list at its exported version, then switches on exactly those mods in the list's order.
    // Other mods stay installed, switched off.
    public async Task ImportAsync(string path)
    {
        if (Importing) return;

        List<PackEntry> wanted;
        try
        {
            wanted = ModPack.Read(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Warn($"Couldn't read that file: {e.Message}");
            return;
        }
        if (wanted.Count == 0)
        {
            Status.Warn("That file doesn't list any mods.");
            return;
        }
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. Mods can't be imported while the game is open.");
            return;
        }

        Importing = true;
        var keys = new List<string>();
        var problems = new List<string>();
        var newer = new List<string>();
        var steamVersion = new List<string>();
        try
        {
            Status.Work("Importing mods", "Looking them up on the Workshop");
            var details = (await workshop.DetailsAsync(wanted.Select(e => e.WorkshopId).OfType<ulong>().Distinct())).ToDictionary(i => i.Id);
            await steam.PrepareAsync(Paths.Mods, new Progress<string>(text => Status.Work("Setting up SteamCMD", text)));

            var found = Scan();
            var subscribed = SteamLibrary.Manifests();
            var downloaded = steam.InstalledManifests();

            for (var i = 0; i < wanted.Count; i++)
            {
                var entry = wanted[i];
                if (entry.WorkshopId is not { } id)
                {
                    if (found.ContainsKey(entry.Key)) keys.Add(entry.Key);
                    else problems.Add($"{entry.Name} (a local mod that isn't in your Mods folder)");
                    continue;
                }

                // A Steam subscription is already installed, and Steam decides its version.
                if (found.ContainsKey(ModFolder.SteamPrefix + id))
                {
                    keys.Add(ModFolder.SteamPrefix + id);
                    if (entry.Manifest != 0 && subscribed.GetValueOrDefault(id) != entry.Manifest) steamVersion.Add(entry.Name);
                    continue;
                }
                if (!details.TryGetValue(id, out var item))
                {
                    problems.Add($"{entry.Name} (it's no longer on the Workshop)");
                    continue;
                }

                var key = id.ToString();
                var manifest = entry.Manifest != 0 ? entry.Manifest : item.Manifest;
                if (found.ContainsKey(key) && downloaded.GetValueOrDefault(id) == manifest)
                {
                    keys.Add(key);
                    continue;
                }
                if (manifest == 0)
                {
                    problems.Add($"{item.Title} (Steam didn't say which version to download)");
                    continue;
                }

                var reason = await DownloadAsync(item, manifest, i, wanted.Count);
                if (reason == SteamCmdOutput.VersionGone && item.Manifest is not 0 && item.Manifest != manifest)
                {
                    reason = await DownloadAsync(item, item.Manifest, i, wanted.Count);
                    if (reason is null) newer.Add(item.Title);
                }
                if (reason is null) keys.Add(key);
                else problems.Add($"{item.Title} ({reason.TrimEnd('.')})");
            }
        }
        catch (Exception e)
        {
            Status.Warn($"The import stopped: {e.Message}");
            Reload();
            return;
        }
        finally
        {
            Importing = false;
            Status.Idle();
        }

        Reload();
        var order = keys.Select(k => Installed.FirstOrDefault(m => m.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).OfType<InstalledMod>().Distinct().ToList();
        var rest = Installed.Except(order).ToList();
        Installed.Clear();
        foreach (var mod in order.Concat(rest))
        {
            mod.Enabled = order.Contains(mod);
            Installed.Add(mod);
        }
        if (!Save()) return;
        SyncTiles();

        var notes = new List<string> { order.Count == 1 ? "Imported 1 mod and switched it on." : $"Imported {order.Count} mods and switched them on in the list's order." };
        if (newer.Count > 0) notes.Add($"Steam no longer has the exported version of {Names(newer)}, so {(newer.Count == 1 ? "it got" : "they got")} the latest.");
        if (steamVersion.Count > 0) notes.Add($"{Names(steamVersion)} {(steamVersion.Count == 1 ? "is a Steam subscription, so it stays" : "are Steam subscriptions, so they stay")} at Steam's version.");
        if (problems.Count > 0) notes.Add($"Couldn't add {string.Join("; ", problems)}.");

        var message = string.Join(" ", notes);
        if (problems.Count > 0 || newer.Count > 0 || steamVersion.Count > 0) Status.Warn(message);
        else Status.Say(message);
    }

    Task<string?> DownloadAsync(WorkshopItem item, ulong manifest, int index, int count)
    {
        var detail = count > 1 ? $"Importing {index + 1} of {count}" : "Importing";
        Status.Work(item.Title, detail, (double)index / count, Find(item.Id));
        var progress = new Progress<double>(p =>
        {
            if (Importing) Status.Work(item.Title, detail, (index + p) / count, Find(item.Id));
        });
        return steam.DownloadVersionAsync(item.Id, manifest, item.Size, Path.Combine(Paths.Mods, item.Id.ToString()), progress);
    }

    static string Names(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names[..^1])} and {names[^1]}",
    };

    public void PlayGame()
    {
        if (GameIsRunning()) Status.Say("UBOAT is already running.");
        else if (!Shell.Open($"steam://rungameid/{Workshop.AppId}")) Status.Warn("Couldn't start UBOAT through Steam. Is Steam installed?");
    }

    public void OpenModsFolder()
    {
        Directory.CreateDirectory(Paths.Mods);
        Shell.Open(Paths.Mods);
    }

    void OnInstalled(ModTile tile)
    {
        var key = tile.Id.ToString();
        var isNew = Installed.All(m => m.Key != key);
        Reload();

        if (isNew && Installed.FirstOrDefault(m => m.Key == key) is { } mod)
        {
            mod.Enabled = true;
            if (!Save()) return;
        }
        Status.Say(isNew ? $"Installed {tile.Title}" : $"Updated {tile.Title}");
    }

    void Show(IReadOnlyList<WorkshopItem> items)
    {
        var next = new Dictionary<ulong, ModTile>(items.Count);
        foreach (var item in items)
        {
            if (next.ContainsKey(item.Id)) continue;
            if (tiles.TryGetValue(item.Id, out var tile)) tile.Refresh(item);
            else tile = new ModTile(item);
            next[item.Id] = tile;
        }

        tiles = next;
        Tiles = [.. next.Values];
        foreach (var mod in Installed) mod.Tile = Find(mod.WorkshopId);
        SyncTiles();
    }

    public void Rescan()
    {
        var found = Scan();
        var merged = ModList.Merge(modList.SavedOrder(), modList.Enabled(), found.Keys);
        var unchanged = merged.Count == Installed.Count && merged.Zip(Installed).All(pair =>
            pair.First.Key == pair.Second.Key
            && pair.First.Enabled == pair.Second.Enabled
            && (found.GetValueOrDefault(pair.First.Key)?.Source ?? ModSource.Missing) == pair.Second.Source);

        if (!unchanged) Reload();
    }

    static Dictionary<string, ModFolder> Scan() =>
        ModFolder.Scan(Paths.Mods, SteamLibrary.WorkshopFolder())
            .DistinctBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

    void Reload()
    {
        var found = Scan();

        Installed.Clear();
        foreach (var (key, enabled) in ModList.Merge(modList.SavedOrder(), modList.Enabled(), found.Keys))
        {
            var folder = found.GetValueOrDefault(key) ?? ModFolder.Missing(key);
            Installed.Add(new InstalledMod(folder) { Enabled = enabled, Tile = Find(folder.WorkshopId) });
        }
        Renumber();
        SyncTiles();
    }

    void Renumber()
    {
        for (var i = 0; i < Installed.Count; i++) Installed[i].Ordinal = (i + 1).ToString();

        var on = Installed.Count(m => m.Enabled);
        InstalledSummary = Installed.Count switch
        {
            0 => "Nothing installed yet.",
            1 => on == 1 ? "1 mod, switched on." : "1 mod, switched off.",
            _ => $"{Installed.Count} mods, {(on == Installed.Count ? "all" : on == 0 ? "none" : on.ToString())} switched on. UBOAT loads them from the top down, so the lower one wins when two change the same thing.",
        };
    }

    void SyncTiles()
    {
        var keys = Installed.Where(m => m.Source != ModSource.Missing).Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var versions = steam.InstalledVersions();
        var manifests = steam.InstalledManifests();

        foreach (var tile in Tiles)
        {
            var outdated = manifests.TryGetValue(tile.Id, out var manifest) && tile.Item.Manifest != 0
                ? manifest != tile.Item.Manifest
                : versions.TryGetValue(tile.Id, out var installed) && tile.Item.Updated > installed;
            tile.Settle(subscribed: keys.Contains(ModFolder.SteamPrefix + tile.Id), installed: keys.Contains(tile.Id.ToString()), outdated);
        }
        CountUpdates();
    }

    void CountUpdates() => UpdateCount = Tiles.Count(t => t.State == TileState.Outdated);

    ModTile? Find(ulong? id) => id is { } value ? tiles.GetValueOrDefault(value) : null;

    static bool GameIsRunning()
    {
        var processes = Process.GetProcessesByName("UBOAT").Concat(Process.GetProcessesByName("UBOAT Launcher")).ToArray();
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
}
