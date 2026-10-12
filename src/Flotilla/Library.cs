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
    readonly string profilesFile = Path.Combine(Paths.AppData, "profiles.json");
    readonly string requirementsFile = Path.Combine(Paths.AppData, "requirements.json");
    readonly SteamCmd steam = new(Path.Combine(Paths.AppData, "steamcmd"));
    readonly Installer installer;
    readonly Dictionary<ulong, RequirementList> requirements;
    readonly HashSet<string> placeAfterInstall = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<ulong, ModTile> tiles = [];
    Profile? installingFor;
    GameLog? lastLog;
    DateTime lastLogTime;
    bool refreshing;

    public Library()
    {
        installer = new Installer(steam, Paths.Mods, Status);
        installer.Installed += OnInstalled;
        installer.Finished += () =>
        {
            installingFor = null;
            placeAfterInstall.Clear();
        };
        foreach (var profile in ProfileStore.Load(profilesFile)) Profiles.Add(new ProfileItem(profile));
        requirements = Requirements.Load(requirementsFile);
        ModTile.FavouriteChanged += OnFavouriteChanged;
    }

    public Settings Settings { get; } = Settings.Load();
    public StatusLine Status { get; } = new();
    public string GameVersion => Settings.GameVersion ?? "not known yet";
    public ObservableCollection<InstalledMod> Installed { get; } = [];
    public ObservableCollection<ProfileItem> Profiles { get; } = [];
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
        await CheckGameLogAsync();
        await RefreshWorkshopAsync();
        _ = ReadRequirementsAsync();

        if (UpdateCount > 0)
            Status.Say(UpdateCount == 1 ? "1 mod has an update. Update it from Installed." : $"{UpdateCount} mods have updates. Update them from Installed.");
    }

    public async Task CheckGameLogAsync()
    {
        if (!Settings.CheckGameLog && !Settings.WarnGameVersion) return;

        var written = File.Exists(Paths.GameLog) ? File.GetLastWriteTimeUtc(Paths.GameLog) : default;
        if (written == default || written == lastLogTime) return;
        if (GameIsRunning())
        {
            await ReadGameVersionAsync();
            return;
        }

        GameLog log;
        try
        {
            log = await Task.Run(() => GameLog.Parse(ReadShared(Paths.GameLog)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var fresh = written != Settings.LogChecked;
        lastLog = log;
        lastLogTime = written;
        Settings.LogChecked = written;
        if (log.GameVersion is { } version) Settings.GameVersion = version;
        Settings.Save();
        Raise(nameof(GameVersion));
        UpdateIssues();

        var broken = Installed.Count(m => m.Enabled && m.IsError);
        if (fresh && broken > 0 && Settings.CheckGameLog)
            Status.Warn(broken == 1 ? "Your last game logged errors from 1 mod. It's marked in Installed." : $"Your last game logged errors from {broken} mods. They're marked in Installed.");
    }

    async Task ReadGameVersionAsync()
    {
        if (Settings.GameVersion is not null) return;
        try
        {
            if (await Task.Run(() => GameLog.Parse(ReadShared(Paths.GameLog).Take(200)).GameVersion) is not { } version) return;
            Settings.GameVersion = version;
            Settings.Save();
            Raise(nameof(GameVersion));
            UpdateIssues();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    bool KnowsRequirements(ulong id) => requirements.TryGetValue(id, out var known) && DateTime.UtcNow - known.Read < TimeSpan.FromDays(3);

    public async Task<IReadOnlyList<ulong>> RequirementsOfAsync(ulong id, bool fresh = false)
    {
        if (fresh || !KnowsRequirements(id)) await FetchRequirementsAsync(id);
        return requirements.TryGetValue(id, out var known) ? known.Ids : [];
    }

    async Task<bool> FetchRequirementsAsync(ulong id)
    {
        try
        {
            requirements[id] = new RequirementList([.. await workshop.RequirementsAsync(id)], DateTime.UtcNow);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }

        try
        {
            Requirements.Save(requirementsFile, requirements);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    public async Task<List<RequirementRow>> RequirementRowsAsync(ulong id)
    {
        var rows = new List<RequirementRow>();
        foreach (var need in await RequirementsOfAsync(id))
        {
            var tile = await TileForAsync(need);
            var copy = InstalledCopy(need);
            var state = copy is null ? "Not installed" : copy.Enabled ? "Installed" : "Installed, switched off";
            rows.Add(new RequirementRow(tile?.Title ?? copy?.Name ?? $"Workshop item {need}", state, copy is { Enabled: true }, tile));
        }
        return rows;
    }

    async Task ReadRequirementsAsync()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            await Task.Delay(pass == 0 ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(3));

            var due = Installed.Where(m => m.Source != ModSource.Missing).Select(m => m.WorkshopId).OfType<ulong>()
                .Distinct().Where(id => !KnowsRequirements(id)).ToList();
            if (due.Count == 0) break;

            var failed = 0;
            foreach (var id in due)
            {
                if (!await FetchRequirementsAsync(id)) failed++;
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            }
            UpdateIssues();
            if (failed == 0) break;
        }
    }

    InstalledMod? InstalledCopy(ulong id) =>
        Installed.Where(m => m.WorkshopId == id && m.Source != ModSource.Missing).OrderByDescending(m => m.Enabled).FirstOrDefault();

    void UpdateIssues()
    {
        var log = Settings.CheckGameLog ? lastLog : null;
        var game = Settings.WarnGameVersion ? Settings.GameVersion : null;

        foreach (var mod in Installed)
        {
            var issues = new List<string>();
            var error = false;
            var fixable = false;

            if (log is not null && mod.Folder.Path is { } path && log.Errors.TryGetValue(mod.Key, out var errors)
                && Directory.GetLastWriteTimeUtc(path) < lastLogTime)
            {
                error = true;
                issues.Add($"Your last game logged errors from it: {errors[0]}");
                issues.AddRange(errors.Skip(1));
            }

            if (mod.Source != ModSource.Missing && mod.WorkshopId is { } id && requirements.TryGetValue(id, out var needs))
            {
                var index = Installed.IndexOf(mod);
                foreach (var need in needs.Ids)
                {
                    var name = Find(need)?.Title ?? InstalledCopy(need)?.Name ?? $"Workshop item {need}";
                    if (InstalledCopy(need) is not { } copy) issues.Add($"Needs {name}, which isn't installed.");
                    else if (mod.Enabled && !copy.Enabled) issues.Add($"Needs {name}, which is switched off.");
                    else if (mod.Enabled && Installed.IndexOf(copy) > index) issues.Add($"Needs {name}, which loads after it. It should load first.");
                    else continue;
                    fixable = true;
                }
            }

            if (game is not null && mod.Folder.Manifest is { GameVersions.Count: > 0 } manifest && !GameLog.Supports(manifest.GameVersions, game))
                issues.Add($"Made for {string.Join(", ", manifest.GameVersions)}, but you have UBOAT {game}. It may not work.");

            mod.IsError = error;
            mod.CanFix = fixable;
            mod.Issue = issues.Count > 0 ? string.Join("\n", issues) : null;
        }
    }

    public async void Fix(InstalledMod mod)
    {
        if (mod.WorkshopId is not { } id) return;
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. Mods can't be changed while the game or its launcher is open.");
            return;
        }

        var needs = await RequirementsOfAsync(id);
        var missing = new List<string>();
        foreach (var need in needs)
        {
            if (InstalledCopy(need) is not null) continue;
            if (await TileForAsync(need) is { CanInstall: true } tile)
            {
                missing.Add(tile.Title);
                installer.Add(tile);
            }
        }

        if (missing.Count > 0) placeAfterInstall.Add(mod.Key);
        PlaceRequirements(mod.Key);
        CountUpdates();
        Status.Say(missing.Count > 0
            ? $"Installing {Names(missing)} for {mod.Name}. {(missing.Count == 1 ? "It goes" : "They go")} above it in the load order."
            : $"{mod.Name} has what it needs, loading after it.");
    }

    void PlaceRequirements(string key)
    {
        if (Installed.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is not { WorkshopId: { } id, Enabled: true } mod) return;
        if (!requirements.TryGetValue(id, out var needs)) return;

        foreach (var need in needs.Ids)
        {
            if (InstalledCopy(need) is not { } copy) continue;
            copy.Enabled = true;
            if (Installed.IndexOf(copy) > Installed.IndexOf(mod)) Installed.Move(Installed.IndexOf(copy), Installed.IndexOf(mod));
        }
        Save();
    }

    async Task<ModTile?> TileForAsync(ulong id)
    {
        if (Find(id) is { } known) return known;
        try
        {
            if ((await workshop.DetailsAsync([id])).FirstOrDefault() is not { } item) return null;
            var tile = new ModTile(item);
            tile.LoadFavourite(Settings.Favourites.Contains(id));
            tiles[id] = tile;
            return tile;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    async Task AddRequirementsAsync(ModTile tile)
    {
        var added = new List<string>();
        foreach (var need in await RequirementsOfAsync(tile.Id, fresh: true))
        {
            if (InstalledCopy(need) is not null) continue;
            if (await TileForAsync(need) is { CanInstall: true } required)
            {
                added.Add(required.Title);
                installer.Add(required);
            }
        }
        placeAfterInstall.Add(tile.Id.ToString());
        CountUpdates();
        if (added.Count > 0) Status.Say($"{tile.Title} needs {Names(added)}, so {(added.Count == 1 ? "that's" : "those are")} installing too.");
    }

    void OnFavouriteChanged(ModTile tile)
    {
        Settings.Favourites.Remove(tile.Id);
        if (tile.IsFavourite) Settings.Favourites.Add(tile.Id);
        Settings.Save();
        Raise(nameof(FavouriteCount));
    }

    public int FavouriteCount => Settings.Favourites.Count;

    public long CacheSize() => Paths.Caches.Select(c => Path.Combine(Paths.Game, c)).Where(Directory.Exists)
        .Sum(dir => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));

    public bool ClearCache(bool quiet = false)
    {
        if (GameIsRunning())
        {
            if (!quiet) Status.Warn("Close UBOAT first. Its cache can't be cleared while the game or its launcher is open.");
            return false;
        }

        long freed = 0;
        var locked = 0;
        foreach (var dir in Paths.Caches.Select(c => Path.Combine(Paths.Game, c)).Where(Directory.Exists))
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList())
            {
                try
                {
                    var size = file.Length;
                    file.Delete();
                    freed += size;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    locked++;
                }
            }
        }

        if (!quiet) Status.Say(locked > 0
            ? $"Cleared {Megabytes(freed)} of UBOAT's cache. {locked} files were in use and stayed."
            : $"Cleared {Megabytes(freed)} of UBOAT's cache. The next load rebuilds it, so it takes a little longer.");
        return true;
    }

    static string Megabytes(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes / (1024.0 * 1024):0} MB";

    public async Task RefreshWorkshopAsync()
    {
        if (refreshing) return;

        refreshing = true;
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
        finally
        {
            refreshing = false;
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
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        } while (await timer.WaitForNextTickAsync());
    }

    public void Install(ModTile tile)
    {
        if (!tile.CanInstall) return;

        var isNew = tile.State is TileState.Available or TileState.Failed;
        installer.Add(tile);
        CountUpdates();
        if (isNew && Settings.InstallRequirements) _ = AddRequirementsAsync(tile);
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
        var count = Arrange(keys);
        if (!Save()) return;
        SyncTiles();

        var notes = new List<string> { count == 1 ? "Imported 1 mod and switched it on." : $"Imported {count} mods and switched them on in the list's order." };
        if (newer.Count > 0) notes.Add($"Steam no longer has the exported version of {Names(newer)}, so {(newer.Count == 1 ? "it got" : "they got")} the latest.");
        if (steamVersion.Count > 0) notes.Add($"{Names(steamVersion)} {(steamVersion.Count == 1 ? "is a Steam subscription, so it stays" : "are Steam subscriptions, so they stay")} at Steam's version.");
        if (problems.Count > 0) notes.Add($"Couldn't add {string.Join("; ", problems)}.");

        var message = string.Join(" ", notes);
        if (problems.Count > 0 || newer.Count > 0 || steamVersion.Count > 0) Status.Warn(message);
        else Status.Say(message);
    }

    int Arrange(IEnumerable<string> keys)
    {
        var order = keys.Select(k => Installed.FirstOrDefault(m => m.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).OfType<InstalledMod>().Distinct().ToList();
        var rest = Installed.Except(order).ToList();
        Installed.Clear();
        foreach (var mod in order.Concat(rest))
        {
            mod.Enabled = order.Contains(mod);
            Installed.Add(mod);
        }
        return order.Count;
    }

    public void SetAll(bool enabled)
    {
        var changed = Installed.Where(m => m.Enabled != enabled && (m.Source != ModSource.Missing || !enabled)).ToList();
        if (changed.Count == 0) return;

        foreach (var mod in changed) mod.Enabled = enabled;
        if (Save()) Status.Say(enabled ? "Switched every mod on." : "Switched every mod off.");
    }

    IEnumerable<string> Available => Installed.Where(m => m.Source != ModSource.Missing).Select(m => m.Key);

    IEnumerable<string> EnabledKeys => Installed.Where(m => m.Enabled && m.Source != ModSource.Missing).Select(m => m.Key);

    public ProfileItem? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public void SaveProfile(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;

        var profile = new Profile(name, [.. EnabledKeys]);
        if (FindProfile(name) is { } existing) existing.Profile = profile;
        else Profiles.Add(new ProfileItem(profile));

        if (!StoreProfiles()) return;
        Status.Say(profile.Mods.Count == 1 ? $"Saved {name} with 1 mod." : $"Saved {name} with {profile.Mods.Count} mods.");
    }

    public void UpdateProfile(ProfileItem item)
    {
        item.Profile = item.Profile with { Mods = [.. EnabledKeys] };
        if (StoreProfiles()) Status.Say($"{item.Name} now has the {item.Profile.Mods.Count} mods that are switched on.");
    }

    public void RenameProfile(ProfileItem item, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == item.Name) return;

        item.Profile = item.Profile with { Name = name };
        StoreProfiles();
    }

    public void DeleteProfile(ProfileItem item)
    {
        Profiles.Remove(item);
        if (StoreProfiles()) Status.Say($"Deleted the {item.Name} profile. Your mods are unchanged.");
    }

    public void MoveProfile(ProfileItem item, int index)
    {
        var from = Profiles.IndexOf(item);
        index = Math.Clamp(index, 0, Profiles.Count - 1);
        if (from < 0 || from == index) return;

        Profiles.Move(from, index);
        StoreProfiles();
    }

    public void ApplyProfile(ProfileItem item)
    {
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. Profiles can't be switched while the game or its launcher is open.");
            return;
        }

        var (on, missing) = ProfileStore.Resolve(Available, item.Profile.Mods);
        Arrange(on);
        if (!Save()) return;
        SyncTiles();

        var installing = new List<string>();
        var unknown = new List<string>();
        foreach (var key in missing)
        {
            var id = ModFolder.Missing(key).WorkshopId;
            if (Find(id) is { CanInstall: true } tile)
            {
                installing.Add(tile.Title);
                installer.Add(tile);
            }
            else unknown.Add(Find(id)?.Title ?? key);
        }
        if (installing.Count > 0) installingFor = item.Profile;
        CountUpdates();

        var notes = new List<string> { $"Switched to {item.Name}." };
        if (installing.Count > 0) notes.Add($"Installing {Names(installing)}, then {(installing.Count == 1 ? "it switches" : "they switch")} on in the profile's order.");
        if (unknown.Count > 0) notes.Add($"Couldn't find {Names(unknown)} on this PC or the Workshop.");

        if (unknown.Count > 0) Status.Warn(string.Join(" ", notes));
        else Status.Say(string.Join(" ", notes));
    }

    bool StoreProfiles()
    {
        try
        {
            ProfileStore.Save(profilesFile, Profiles.Select(p => p.Profile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Warn($"Couldn't save your profiles: {e.Message}");
            return false;
        }
        MarkProfiles();
        return true;
    }

    void MarkProfiles()
    {
        var available = Available.ToList();
        var enabled = EnabledKeys.ToList();
        foreach (var item in Profiles) item.IsActive = ProfileStore.IsActive(item.Profile, available, enabled);
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
        if (GameIsRunning())
        {
            Status.Say("UBOAT is already running.");
            return;
        }

        if (Settings.ClearCacheBeforePlay) ClearCache(quiet: true);
        if (!Shell.Open($"steam://rungameid/{Workshop.AppId}")) Status.Warn("Couldn't start UBOAT through Steam. Is Steam installed?");
        else if (Settings.ClearCacheBeforePlay) Status.Say("Cleared UBOAT's cache and started the game. The first load takes a little longer while it rebuilds.");
    }

    public void OpenModsFolder()
    {
        Directory.CreateDirectory(Paths.Mods);
        Shell.Open(Paths.Mods);
    }

    void OnInstalled(ModTile tile)
    {
        var key = tile.Id.ToString();
        var isNew = Installed.All(m => m.Key != key || m.Source == ModSource.Missing);
        Reload();

        if (installingFor is { } profile && ProfileStore.Resolve([key], profile.Mods).On.Count > 0)
        {
            Arrange(ProfileStore.Resolve(Available, profile.Mods).On);
            if (!Save()) return;
        }
        else if (isNew && Installed.FirstOrDefault(m => m.Key == key) is { } mod)
        {
            mod.Enabled = true;
            if (!Save()) return;
        }
        foreach (var dependent in placeAfterInstall.ToList()) PlaceRequirements(dependent);
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
            tile.LoadFavourite(Settings.Favourites.Contains(item.Id));
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
        _ = CheckGameLogAsync();
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
        MarkProfiles();
        UpdateIssues();
    }

    public void SettingsChanged()
    {
        Settings.Save();
        UpdateIssues();
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
