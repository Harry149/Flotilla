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
    readonly string historyFile = Path.Combine(Paths.AppData, "history.json");
    readonly string bisectFile = Path.Combine(Paths.AppData, "bisect.json");
    readonly string backups = Path.Combine(Paths.AppData, "backups");
    readonly Community community = new(Web.Client);
    Dictionary<string, List<string>> settingsFiles = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<ulong, VoteCount> votes = [];
    Bisect? bisect;
    Profile? installingFor;
    GameLog? lastLog;
    DateTime lastLogTime;
    bool checkingLog;
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
    public string ToggleAllLabel { get; private set => Set(ref field, value); } = "Turn all on";
    bool allOn;
    public bool Importing
    {
        get;
        private set
        {
            if (Set(ref field, value)) Raise(nameof(CanImport));
        }
    }
    public bool CanImport => !Importing;
    public bool Busy => installer.Busy || Importing;

    public async Task StartAsync()
    {
        Show(Workshop.Load(catalogue));
        Reload();
        ShowSnapshots(History.Load(historyFile));
        ResumeBisect();
        Remember("Opened Flotilla");
        _ = FindSettingsAsync();
        _ = WatchFeaturedAsync();
        _ = WatchGameAsync();
        await CheckGameLogAsync();
        await RefreshWorkshopAsync();
        _ = LoadVotesAsync();
        _ = ReadRequirementsAsync();

        if (UpdateCount > 0)
            Status.Say(UpdateCount == 1 ? "1 mod has an update. Update it from Installed." : $"{UpdateCount} mods have updates. Update them from Installed.");
    }

    public event Action<GameAlert>? ProblemsFound;

    async Task WatchGameAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var wasRunning = GameIsRunning();
        while (await timer.WaitForNextTickAsync())
        {
            var running = GameIsRunning();
            if (wasRunning && !running && Settings.CheckGameLog)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await CheckGameLogAsync();
            }
            wasRunning = running;
        }
    }

    public async Task CheckGameLogAsync()
    {
        var written = File.Exists(Paths.GameLog) ? File.GetLastWriteTimeUtc(Paths.GameLog) : default;
        if (written == default || written == lastLogTime || checkingLog) return;

        checkingLog = true;
        GameLog log;
        try
        {
            await ReadGameVersionAsync();
            if (!Settings.CheckGameLog || GameIsRunning()) return;

            var mods = Installed.Where(m => m.Source != ModSource.Missing).Select(m => (m.Key, m.Name, m.Folder.Path)).ToList();
            log = await Task.Run(() => GameLog.Parse(ReadShared(Paths.GameLog), ModFingerprints.From(mods)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        finally
        {
            checkingLog = false;
        }

        var fresh = written != Settings.LogChecked;
        lastLog = log;
        lastLogTime = written;
        Settings.LogChecked = written;
        Settings.Save();
        UpdateIssues();

        var broken = Installed.Where(m => m.Enabled && m.IsError).ToList();
        if (!fresh || broken.Count == 0) return;

        var names = Installed.ToDictionary(m => m.Key, m => m.Name, StringComparer.OrdinalIgnoreCase);
        var warnings = broken.Sum(m => log.ByMod.GetValueOrDefault(m.Key)?.Count(p => p.Severity == Severity.Warning) ?? 0);
        ProblemsFound?.Invoke(new GameAlert(
            [.. broken.Select(m => (m.Name, log.Errors(m.Key).First().Summary))],
            warnings,
            log.Report(names, written)));
    }

    async Task ReadGameVersionAsync()
    {
        try
        {
            if (await Task.Run(() => GameLog.Parse(ReadShared(Paths.GameLog).Take(200)).GameVersion) is not { } version) return;
            if (version == Settings.GameVersion) return;

            Settings.GameVersion = version;
            Settings.Save();
            Raise(nameof(GameVersion));
            foreach (var tile in Tiles) tile.ShowGame(version);
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

            if (log is not null && mod.Folder.Path is { } path && log.Errors(mod.Key).ToList() is { Count: > 0 } errors
                && Directory.GetLastWriteTimeUtc(path) < lastLogTime)
            {
                error = true;
                issues.Add($"Your last game logged errors from it: {errors[0].Summary}");
                issues.AddRange(errors.Skip(1).Select(e => e.Summary));
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
            tile.ShowGame(Settings.GameVersion);
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

    static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };

    public long CacheSize() => Paths.Caches.Select(c => Path.Combine(Paths.Game, c)).Where(Directory.Exists)
        .Sum(dir => new DirectoryInfo(dir).EnumerateFiles("*", Everything).Sum(f => f.Length));

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
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", Everything).ToList())
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

    public async void Install(ModTile tile)
    {
        if (!tile.CanInstall) return;

        var isNew = tile.State is TileState.Available or TileState.Failed;
        if (!isNew) await BackUpAsync(tile);
        installer.Add(tile);
        CountUpdates();
        if (isNew && Settings.InstallRequirements) _ = AddRequirementsAsync(tile);
    }

    public async void UpdateAll()
    {
        foreach (var tile in Tiles.Where(t => t.State == TileState.Outdated).ToList())
        {
            await BackUpAsync(tile);
            installer.Add(tile);
        }
        CountUpdates();
    }

    const long BiggestBackup = 1536L * 1024 * 1024;

    sealed record BackupInfo(ulong Manifest, string Name, DateTime Taken, bool HasFiles);

    string BackupOf(ulong id) => Path.Combine(backups, id.ToString());

    BackupInfo? ReadBackup(ulong id)
    {
        try
        {
            var info = Path.Combine(BackupOf(id), "backup.json");
            return File.Exists(info) ? JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(info)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    async Task BackUpAsync(ModTile tile)
    {
        var folder = Path.Combine(Paths.Mods, tile.Id.ToString());
        if (!Directory.Exists(folder) || !steam.InstalledManifests().TryGetValue(tile.Id, out var manifest) || manifest == tile.Item.Manifest) return;

        Status.Work(tile.Title, "Keeping a copy of this version, so you can go back to it");
        try
        {
            await Task.Run(() =>
            {
                var target = BackupOf(tile.Id);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);

                var size = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                var copy = size <= BiggestBackup;
                if (copy) Folders.Copy(folder, Path.Combine(target, "files"));
                File.WriteAllText(Path.Combine(target, "backup.json"), JsonSerializer.Serialize(new BackupInfo(manifest, tile.Title, DateTime.UtcNow, copy)));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            Status.Idle();
        }
    }

    public string? RollbackNote(InstalledMod mod) =>
        mod is { Source: ModSource.Download, WorkshopId: { } id } && ReadBackup(id) is { } backup
            ? $"Go back to the version you had before {backup.Taken.ToLocalTime():d MMM}"
            : null;

    public async Task RollBackAsync(InstalledMod mod)
    {
        if (mod is not { Source: ModSource.Download, WorkshopId: { } id } || ReadBackup(id) is not { } backup) return;
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. Mods can't be changed while the game is open.");
            return;
        }

        var folder = Path.Combine(Paths.Mods, id.ToString());
        var files = Path.Combine(BackupOf(id), "files");
        try
        {
            if (backup.HasFiles && Directory.Exists(files))
            {
                await Task.Run(() => Folders.Replace(folder, files));
                steam.Pin(id, backup.Manifest);
            }
            else
            {
                Status.Work(mod.Name, "Downloading the version you had before");
                var tile = Find(id);
                var reason = await steam.DownloadVersionAsync(id, backup.Manifest, tile?.Item.Size ?? 0, folder,
                    new Progress<double>(p => Status.Work(mod.Name, "Downloading the version you had before", p, tile)));
                Status.Idle();
                if (reason is not null)
                {
                    Status.Warn($"Couldn't go back to the earlier version of {mod.Name}: {reason}.");
                    return;
                }
            }
            Directory.Delete(BackupOf(id), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status.Warn($"Couldn't go back to the earlier version of {mod.Name}: {e.Message}");
            return;
        }

        Reload();
        Remember("Rolled back a mod");
        Status.Say($"{mod.Name} is back to the version you had before. Update shows again when you want the new one.");
    }

    public bool IsBisecting => bisect is not null;
    public string BisectTitle { get; private set => Set(ref field, value); } = "";
    public string BisectText { get; private set => Set(ref field, value); } = "";

    IEnumerable<string> NeedsOf(string key)
    {
        var mod = Installed.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (mod?.WorkshopId is not { } id || !requirements.TryGetValue(id, out var needs)) return [];
        return needs.Ids.Select(InstalledCopy).OfType<InstalledMod>().Select(m => m.Key);
    }

    public bool StartBisect()
    {
        var on = Installed.Where(m => m.Enabled && m.Source != ModSource.Missing).ToList();
        if (on.Count < 2)
        {
            Status.Warn("Switch on at least two mods first. With one mod on, that one is the answer.");
            return false;
        }
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first, then start the search.");
            return false;
        }

        var original = on.Select(m => new PackEntry(m.Key, 0, m.Name)).ToList();
        Remember("Before finding a broken mod");
        bisect = Bisect.Start(original, [.. original.Select(m => m.Key)], NeedsOf);
        return ApplyBisect();
    }

    public void AnswerBisect(bool stillBroken)
    {
        if (bisect is null) return;
        bisect = bisect.Answer(stillBroken, NeedsOf);
        ApplyBisect();
    }

    public void StopBisect(bool turnOffCulprit = false)
    {
        if (bisect is not { } search) return;

        var culprit = turnOffCulprit ? search.Culprit : null;
        var original = new HashSet<string>(search.Original.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
        if (culprit is not null) original.Remove(culprit);
        foreach (var mod in Installed) mod.Enabled = original.Contains(mod.Key);

        bisect = null;
        try
        {
            File.Delete(bisectFile);
        }
        catch (IOException) { }
        Raise(nameof(IsBisecting));
        if (Save("Finished finding a broken mod"))
            Status.Say(culprit is null ? "Your mods are back the way they were." : $"Your mods are back the way they were, with {NameOf(culprit)} switched off.");
    }

    string NameOf(string key) => Installed.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Name ?? key;

    bool ApplyBisect()
    {
        if (bisect is not { } search) return false;

        var on = new HashSet<string>(search.Culprit is null ? search.EnabledNow() : search.Original.Select(m => m.Key).Where(k => !k.Equals(search.Culprit, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in Installed) mod.Enabled = on.Contains(mod.Key);
        if (!Save("Finding a broken mod")) return false;

        try
        {
            search.Save(bisectFile);
        }
        catch (IOException) { }

        if (search.Culprit is { } culprit)
        {
            BisectTitle = $"Found it: {NameOf(culprit)}";
            BisectText = $"Every test points at {NameOf(culprit)}. It's switched off now and everything else is on, so play once more to check. "
                + "If the problem's gone, keep it off and tell its author. If it's still there, two mods may be clashing, so put everything back and ask in the mod's comments.";
        }
        else
        {
            var testing = search.Testing.Count(k => search.Suspects.Contains(k, StringComparer.OrdinalIgnoreCase));
            BisectTitle = $"Finding a broken mod: test {search.Round} of about {search.Round - 1 + search.RoundsLeft}";
            BisectText = $"{search.Suspects.Count} mods could be the cause. {testing} of them are on for this test. Play UBOAT and try what went wrong, then come back and say whether it still happens.";
        }
        Raise(nameof(BisectFound));
        Raise(nameof(IsBisecting));
        Raise(nameof(BisectTitle));
        return true;
    }

    public bool BisectFound => bisect?.Culprit is not null;

    public void ResumeBisect()
    {
        if (Bisect.Load(bisectFile) is not { } saved) return;
        bisect = saved;
        ApplyBisect();
    }

    public string? GameVersionKnown => Settings.GameVersion;

    async Task LoadVotesAsync()
    {
        if (!Sailors.Enabled || Settings.GameVersion is not { } game) return;
        try
        {
            votes = await community.VotesAsync(game);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return;
        }
        foreach (var tile in Tiles) tile.ShowVotes(votes.GetValueOrDefault(tile.Id), MyVote(tile.Id));
    }

    bool? MyVote(ulong id) => Settings.GameVersion is { } game && Settings.Votes.TryGetValue($"{id}|{game}", out var works) ? works : null;

    public async Task VoteAsync(ModTile tile, bool works)
    {
        if (Settings.GameVersion is not { } game)
        {
            Status.Say("Play UBOAT once with Flotilla open first, so it knows your game version.");
            return;
        }

        var previous = MyVote(tile.Id);
        bool? next = previous == works ? null : works;
        var count = votes.GetValueOrDefault(tile.Id) ?? new VoteCount(0, 0);
        count = new VoteCount(
            count.Works - (previous == true ? 1 : 0) + (next == true ? 1 : 0),
            count.Broken - (previous == false ? 1 : 0) + (next == false ? 1 : 0));

        votes[tile.Id] = count;
        var key = $"{tile.Id}|{game}";
        if (next is { } vote) Settings.Votes[key] = vote;
        else Settings.Votes.Remove(key);
        Settings.Save();
        tile.ShowVotes(count, next);

        try
        {
            if (!await community.VoteAsync(Settings.InstallId, tile.Id, game, next)) Status.Warn("Couldn't send your vote. Try again in a moment.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Status.Warn("Couldn't send your vote. Check your internet connection.");
        }
    }

    public async Task<string?> ShareAsync(IReadOnlyList<PackEntry> setup)
    {
        if (setup.Count == 0)
        {
            Status.Warn("There's nothing to share. Switch on some mods first.");
            return null;
        }
        try
        {
            if (await community.ShareAsync(Settings.InstallId, ModPack.Write(setup)) is { } code) return code;
            Status.Warn("Couldn't make a share code right now. Try again in a few minutes.");
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Status.Warn($"Couldn't make a share code: {e.Message}");
            return null;
        }
    }

    public List<PackEntry> ProfileSetup(ProfileItem item)
    {
        var (on, missing) = ProfileStore.Resolve(Available, item.Profile.Mods);
        var installed = on.Select(k => Installed.First(m => m.Key.Equals(k, StringComparison.OrdinalIgnoreCase)));
        return [.. Entries(installed), .. missing.Select(k => new PackEntry(k.Replace(ModFolder.SteamPrefix, ""), 0, Find(ModFolder.Missing(k).WorkshopId)?.Title ?? k))];
    }

    public async Task<List<PackEntry>?> ReadShareAsync(string code)
    {
        try
        {
            return await community.ReadShareAsync(code) is { } body ? ModPack.Read(body) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Status.Warn($"Couldn't read that code: {e.Message}");
            return null;
        }
    }

    public string CompareSetup(IReadOnlyList<PackEntry> wanted)
    {
        var now = CurrentSetup();
        var difference = History.Compare(now, wanted);
        if (difference.Same) return "You already have exactly this setup.";

        var lines = new List<string>();
        var missing = wanted.Where(e => e.WorkshopId is { } id ? InstalledCopy(id) is null : Installed.All(m => !m.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase))).Select(e => e.Name).ToList();
        if (missing.Count > 0) lines.Add($"To install: {Names(missing)}.");
        var turnOn = difference.Added.Except(missing).ToList();
        if (turnOn.Count > 0) lines.Add($"To switch on: {Names(turnOn)}.");
        if (difference.Removed.Count > 0) lines.Add($"To switch off: {Names([.. difference.Removed])}.");
        if (difference.Versions.Count > 0) lines.Add($"At a different version: {Names([.. difference.Versions])}.");
        if (difference.Reordered) lines.Add("The load order is different too.");
        return string.Join("\n", lines);
    }

    public IReadOnlyList<string> SettingsFilesOf(InstalledMod mod) => settingsFiles.GetValueOrDefault(mod.Key) ?? [];

    async Task FindSettingsAsync()
    {
        var mods = Installed.Where(m => m.Source != ModSource.Missing).Select(m => (m.Key, m.Folder.Path)).ToList();
        try
        {
            settingsFiles = await Task.Run(() => SettingsFile.Find(Paths.Game, mods));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var mod in Installed) mod.HasSettings = settingsFiles.ContainsKey(mod.Key);
    }

    public async Task<List<ChangeNote>> ChangeNotesAsync(ulong id)
    {
        try
        {
            return await workshop.ChangeNotesAsync(id);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }

    public DateTime? InstalledOn(ulong id) => steam.InstalledVersions().TryGetValue(id, out var time) ? time : null;

    public void Move(InstalledMod mod, int index)
    {
        var from = Installed.IndexOf(mod);
        index = Math.Clamp(index, 0, Installed.Count - 1);
        if (from < 0 || from == index) return;

        Installed.Move(from, index);
        Save();
    }

    public bool Save(string reason = "Changed mods")
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
        SaveSteamOrder();
        Renumber();
        Remember(reason);
        return true;
    }

    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];

    void Remember(string reason)
    {
        if (bisect is not null) return;

        var list = Snapshots.Select(s => s.Snapshot).ToList();
        if (!History.Record(list, new Snapshot(DateTime.UtcNow, reason, CurrentSetup()))) return;
        try
        {
            History.Save(historyFile, list);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        ShowSnapshots(list);
    }

    void ShowSnapshots(List<Snapshot> list)
    {
        Snapshots.Clear();
        var now = CurrentSetup();
        foreach (var snapshot in list) Snapshots.Add(new SnapshotItem(snapshot, History.Compare(now, snapshot.Mods)));
    }

    public async Task RestoreAsync(SnapshotItem item)
    {
        if (await ApplySetupAsync(item.Snapshot.Mods, "Restored", "Restored an earlier setup"))
            Status.Say($"Restored your setup from {item.When}.");
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

    public List<PackEntry> CurrentSetup() => Entries(Installed.Where(m => m.Enabled && m.Source != ModSource.Missing));

    List<PackEntry> Entries(IEnumerable<InstalledMod> chosen)
    {
        var subscribed = SteamLibrary.Manifests();
        var downloaded = steam.InstalledManifests();
        return chosen.Select(m => m switch
        {
            { Source: ModSource.Steam, WorkshopId: { } id } => new PackEntry(id.ToString(), subscribed.GetValueOrDefault(id), m.Name),
            { Source: ModSource.Download, WorkshopId: { } id } => new PackEntry(id.ToString(), downloaded.GetValueOrDefault(id), m.Name),
            _ => new PackEntry(m.Key, 0, m.Name),
        }).ToList();
    }

    public void Export(string path)
    {
        var mods = CurrentSetup();
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
        await ApplySetupAsync(wanted, "Imported", "Imported a mod list");
    }

    public async Task<bool> ApplySetupAsync(IReadOnlyList<PackEntry> wanted, string verb, string reason)
    {
        if (Importing) return false;
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. Mods can't be changed while the game is open.");
            return false;
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

                var failure = await DownloadAsync(item, manifest, i, wanted.Count);
                if (failure == SteamCmdOutput.VersionGone && item.Manifest is not 0 && item.Manifest != manifest)
                {
                    failure = await DownloadAsync(item, item.Manifest, i, wanted.Count);
                    if (failure is null) newer.Add(item.Title);
                }
                if (failure is null) keys.Add(key);
                else problems.Add($"{item.Title} ({failure.TrimEnd('.')})");
            }
        }
        catch (Exception e)
        {
            Status.Warn($"That stopped part way: {e.Message}");
            Reload();
            return false;
        }
        finally
        {
            Importing = false;
            Status.Idle();
        }

        Reload();
        var count = Arrange(keys);
        if (!Save(reason)) return false;
        SyncTiles();

        var notes = new List<string> { count == 1 ? $"{verb} 1 mod and switched it on." : $"{verb} {count} mods and switched them on in order." };
        if (newer.Count > 0) notes.Add($"Steam no longer has the saved version of {Names(newer)}, so {(newer.Count == 1 ? "it got" : "they got")} the latest.");
        if (steamVersion.Count > 0) notes.Add($"{Names(steamVersion)} {(steamVersion.Count == 1 ? "is a Steam subscription, so it stays" : "are Steam subscriptions, so they stay")} at Steam's version.");
        if (problems.Count > 0) notes.Add($"Couldn't add {string.Join("; ", problems)}.");

        var message = string.Join(" ", notes);
        if (problems.Count > 0 || newer.Count > 0 || steamVersion.Count > 0) Status.Warn(message);
        else Status.Say(message);
        return true;
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

    void SaveSteamOrder()
    {
        var steamMods = Installed.Where(m => m.Source == ModSource.Steam && m.WorkshopId is not null).Select(m => (m.WorkshopId!.Value, m.Enabled)).ToList();
        if (!SteamOrder.Save(steamMods)) return;

        Settings.SteamOrderSaved = DateTime.UtcNow;
        Settings.Save();
    }

    async Task RestartSteamIfBehindAsync()
    {
        SaveSteamOrder();
        if (SteamOrder.SteamStarted() is not { } started || started >= Settings.SteamOrderSaved) return;

        Status.Work("Restarting Steam", "So Steam loads your mods in the same order as Flotilla");
        Shell.Open("steam://exit");
        for (var i = 0; i < 120 && SteamOrder.SteamStarted() is not null; i++) await Task.Delay(500);
        SaveSteamOrder();
        Status.Idle();
    }

    public async void PlayGame()
    {
        if (GameIsRunning())
        {
            Status.Say("UBOAT is already running.");
            return;
        }

        if (Settings.ClearCacheBeforePlay) ClearCache(quiet: true);
        await RestartSteamIfBehindAsync();
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
            tile.ShowGame(Settings.GameVersion);
            if (votes.TryGetValue(item.Id, out var count)) tile.ShowVotes(count, MyVote(item.Id));
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
            var mod = new InstalledMod(folder) { Enabled = enabled, Tile = Find(folder.WorkshopId), HasSettings = settingsFiles.ContainsKey(key) };
            mod.RollbackNote = RollbackNote(mod);
            Installed.Add(mod);
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
            0 => "",
            1 => on == 1 ? "1 mod, switched on" : "1 mod, switched off",
            _ => $"{Installed.Count} mods, {(on == Installed.Count ? "all" : on == 0 ? "none" : on.ToString())} switched on",
        };
        var usable = Installed.Count(m => m.Source != ModSource.Missing);
        var usableOn = Installed.Count(m => m.Enabled && m.Source != ModSource.Missing);
        allOn = usable > 0 && usableOn == usable;
        ToggleAllLabel = usableOn == 0 ? "Turn all on" : allOn ? "Turn all off" : "Turn on all mods";
        MarkProfiles();
        UpdateIssues();
    }

    public void ToggleAll() => SetAll(!allOn);

    public async Task<OrderPlan?> BestOrderAsync()
    {
        var on = Installed.Where(m => m.Enabled && m.Source != ModSource.Missing).ToList();
        if (on.Count < 2)
        {
            Status.Say("Switch on at least two mods first. With one mod on, there's no order to choose.");
            return null;
        }

        Status.Work("Finding the best load order", "Checking what your mods need and what they change");
        var mods = new List<OrderMod>();
        try
        {
            foreach (var mod in on)
            {
                IReadOnlyList<ulong> needs = mod.WorkshopId is { } id ? await RequirementsOfAsync(id) : [];
                var (sheets, text) = await Task.Run(() =>
                {
                    try
                    {
                        return LoadOrder.Contents(mod.Folder.Path!);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);
                    }
                });
                var about = mod.Tile?.Item.Description ?? mod.Folder.Manifest?.Description ?? "";
                mods.Add(new OrderMod(mod.Key, mod.Name, about, mod.WorkshopId, needs, sheets, text));
            }
        }
        finally
        {
            Status.Idle();
        }

        var (order, reasons) = await Task.Run(() => LoadOrder.Best(mods));
        var slots = on.Select(m => Installed.IndexOf(m)).ToList();
        var ordered = order.Select(key => on.First(m => m.Key == key)).ToList();
        var rows = ordered.Select((mod, i) =>
        {
            var was = Installed.IndexOf(mod) + 1;
            var now = slots[i] + 1;
            var move = now < was ? $"Up from {was}" : now > was ? $"Down from {was}" : null;
            return new OrderRow(now.ToString(), mod.Name, move is null ? null : reasons.GetValueOrDefault(mod.Key), move);
        }).ToList();
        return new OrderPlan(ordered, rows, rows.Count(r => r.Move is not null));
    }

    public void UseOrder(OrderPlan plan)
    {
        if (GameIsRunning())
        {
            Status.Warn("Close UBOAT first. The load order can't be changed while the game or its launcher is open.");
            return;
        }

        var all = Installed.ToList();
        var slots = all.Select((mod, i) => (mod, i)).Where(x => plan.Order.Contains(x.mod)).Select(x => x.i).ToList();
        if (slots.Count != plan.Order.Count) return;
        for (var i = 0; i < slots.Count; i++) all[slots[i]] = plan.Order[i];

        Installed.Clear();
        foreach (var mod in all) Installed.Add(mod);
        if (Save("Used the best load order"))
            Status.Say(plan.Moves == 1 ? "Moved 1 mod into the best order. Your old order is in History." : $"Moved {plan.Moves} mods into the best order. Your old order is in History.");
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

    public static bool GameIsRunning()
    {
        var processes = Process.GetProcessesByName("UBOAT").Concat(Process.GetProcessesByName("UBOAT Launcher")).ToArray();
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
}

public sealed record OrderRow(string Number, string Name, string? Reason, string? Move);

public sealed record OrderPlan(List<InstalledMod> Order, List<OrderRow> Rows, int Moves);
