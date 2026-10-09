using System.Collections.Concurrent;
using System.Diagnostics;
using Flotilla.Mods;
using Flotilla.Steam;
using Flotilla.Text;

var failed = 0;
var passed = 0;
var temp = Directory.CreateTempSubdirectory("flotilla-tests").FullName;

void Check(bool ok, string what)
{
    if (ok) passed++;
    else failed++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
}

string Show(IEnumerable<(string Key, bool Enabled)> mods) => string.Join(" ", mods.Select(m => m.Enabled ? m.Key : $"({m.Key})"));

Console.WriteLine("Load order");
Check(Show(ModList.Merge(["A", "B"], ["A", "X", "B"], ["A", "B", "X"])) == "A X B", "the game's own order is kept exactly");
Check(Show(ModList.Merge(["A", "B", "C"], ["A", "C"], ["A", "B", "C"])) == "A (B) C", "a disabled mod keeps its place");
Check(Show(ModList.Merge(["A", "B", "C", "D"], ["C", "A"], ["A", "B", "C", "D"])) == "C (D) A (B)", "disabled mods follow the mod above them when the launcher reorders");
Check(Show(ModList.Merge(["Z", "A"], ["A"], ["Z", "A"])) == "(Z) A", "a disabled mod above every enabled one stays on top");
Check(Show(ModList.Merge([], ["steam:1"], ["steam:1", "uboatslots"])) == "steam:1 (uboatslots)", "a newly found mod is added at the bottom, switched off");
Check(Show(ModList.Merge(["A", "Gone"], ["A"], ["A"])) == "A", "a disabled mod whose files are gone is dropped");
Check(Show(ModList.Merge([], ["steam:9"], [])) == "steam:9", "an enabled mod with no files stays, so it can be shown as missing");
Check(Show(ModList.Merge([], ["mod", "MOD"], ["Mod"])) == "mod", "keys are matched without case and never doubled");

string[] userList = ["steam:3815285055", "steam:3744719470", "steam:3798112566", "steam:3761081574", "steam:3746846377"];
Check(Show(ModList.Merge([], userList, userList.Reverse())) == string.Join(" ", userList), "a first run keeps an existing modlist.txt as it is");

Console.WriteLine("modlist.txt");
var game = Path.Combine(temp, "UBOAT", "modlist.txt");
var order = Path.Combine(temp, "order.txt");
Directory.CreateDirectory(Path.GetDirectoryName(game)!);
File.WriteAllText(game, string.Join("\n", userList) + "\n");
var list = new ModList(game, order);
Check(list.Enabled().SequenceEqual(userList), "reads the game's file");
list.Save([(userList[0], true), (userList[1], false), (userList[2], true), ("uboatslots", true), (userList[3], true), (userList[4], true)]);
var written = File.ReadAllText(game);
Check(written == string.Join("\n", [userList[0], userList[2], "uboatslots", userList[3], userList[4]]), "writes only enabled mods, in order, with the file's own line endings");
Check(File.ReadAllText(game + ".bak") == string.Join("\n", userList) + "\n", "backs up the original before the first write");
Check(list.SavedOrder().Count == 6 && list.SavedOrder()[1] == userList[1], "remembers disabled mods and their place");
list.Save([(userList[0], true)]);
Check(File.ReadAllText(game + ".bak") == string.Join("\n", userList) + "\n", "keeps the first backup rather than overwriting it");
Check(!File.Exists(game + ".tmp"), "leaves no temporary file behind");
var fresh = Path.Combine(temp, "fresh", "modlist.txt");
new ModList(fresh, Path.Combine(temp, "fresh-order.txt")).Save([("a", true), ("b", true)]);
Check(File.ReadAllText(fresh) == "a\r\nb", "a new file uses Windows line endings");
var launcher = Path.Combine(temp, "launched", "Launcher", "launcherdata");
Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
File.WriteAllText(launcher, """{"modList":[{"modName":"a","lastVersion":"1"},{"modName":"other"},{"modName":"B"}],"muted":false}""");
new ModList(Path.Combine(temp, "launched", "modlist.txt"), Path.Combine(temp, "launched-order.txt")).Save([("b", true), ("a", true)]);
var launcherText = File.ReadAllText(launcher);
Check(launcherText.IndexOf("\"B\"") < launcherText.IndexOf("\"a\"") && launcherText.IndexOf("\"a\"") < launcherText.IndexOf("\"other\"") && launcherText.Contains("lastVersion") && launcherText.Contains("muted"), "the launcher's own list is put in the same order, keeping its other data");

Console.WriteLine("BBCode");
var bold = BBCode.Parse("[b]Bold[/b] text");
Check(bold.Children.Count == 2 && bold.Children[0].Tag == "b" && bold.Children[0].PlainText == "Bold" && bold.Children[1].Value == " text", "bold and text");
var bullets = BBCode.Parse("[list]\n[*]One\n[*]Two[/list]after");
Check(bullets.Children[0].Children.Count(c => c.Tag == "*") == 2 && bullets.Children[1].Value == "after", "list items close each other and the list");
Check(BBCode.Parse("[foo]x[/foo]").PlainText == "[foo]x[/foo]", "unknown tags stay as text");
Check(BBCode.Parse("a[/b]").PlainText == "a[/b]", "a stray closing tag stays as text");
Check(BBCode.Parse("[noparse][b]x[/b][/noparse]").PlainText == "[b]x[/b]", "noparse keeps its contents literal");
var link = BBCode.Parse("[url=\"https://example.com/a\"]Site[/url]").Children[0];
Check(link.Tag == "url" && link.Value == "https://example.com/a" && link.PlainText == "Site", "link with a quoted target");
Check(BBCode.Parse("[IMG]https://x/y.png[/IMG]").Children[0] is { Tag: "img", PlainText: "https://x/y.png" }, "tags are case-insensitive");
Check(BBCode.Parse("[s]old[/s]").Children[0].Tag == "strike", "[s] is strike");
var tangled = BBCode.Parse("[b][i]x[/b]y[/i]");
Check(tangled.Children[0].Tag == "b" && tangled.PlainText == "xy[/i]", "a mis-nested close shuts the inner tag as well");
var real = BBCode.Parse("[img]https://i.imgur.com/a.gif[/img]\r\n\r\n[h1]USING THE MOD? PLEASE RATE IT[/h1]\r\nA [url=https://a.b][b]link[/b][/url].\r\n[list]\r\n[*][b]Host[/b] - pick a save.\r\n[*]Join[/list][hr][/hr][strike]Weed[/strike] fund");
Check(real.Children.Select(c => c.Tag).SequenceEqual(["img", "", "h1", "", "url", "", "list", "hr", "strike", ""]), "a real Workshop description parses into the expected blocks");
Check(!real.PlainText.Contains('\r') && !real.PlainText.Contains("[h1]") && !real.PlainText.Contains("[*]"), "line endings normalised and no markup left in the text");

Check(real.Prose.Trim().StartsWith("A link.") && !real.Prose.Contains("http") && !real.Prose.Contains("USING THE MOD"), "the summary text skips images, headings and bare links");
Check(BBCode.Parse("[list][*]One[*]Two[/list]").Prose.Trim() == "One Two", "list items are spaced in the summary text");

Console.WriteLine("SteamCMD output");
Check(SteamCmdOutput.Read("[2026-10-09 10:00:01] Downloading item 2114304535 ...") is { Id: 2114304535, State: ItemState.Downloading }, "download started");
Check(SteamCmdOutput.Read("Success. Downloaded item 2114304535 to \"C:\\x\\steamapps\\workshop\\content\\494840\\2114304535\" (2771 bytes) ") is { Id: 2114304535, State: ItemState.Done }, "download finished");
Check(SteamCmdOutput.Read("ERROR! Download item 7 failed (Failure).") is { Id: 7, State: ItemState.Failed, Reason: "Steam reported a failure" }, "download failed");
Check(SteamCmdOutput.Read("ERROR! Timeout downloading item 8") is { Id: 8, State: ItemState.Failed, Reason: "the download timed out" }, "download timed out");
Check(SteamCmdOutput.Read("ERROR! Not logged on.") is { Id: 0, State: ItemState.Failed }, "login refused");
Check(SteamCmdOutput.Read("Loading Steam API...OK") is null, "other lines are ignored");
Check(SteamCmdOutput.ReadDepot("Depot download complete : \"C:\\s\\steamapps\\content\\app_494840\\depot_494840\" (manifest 3024076959896569762) ") is { State: ItemState.Done }, "a version download finished");
Check(SteamCmdOutput.ReadDepot("Depot download failed : Failed downloading 1 manifests (Manifest unavailable) ") is { State: ItemState.Failed, Reason: SteamCmdOutput.VersionGone }, "a version Steam no longer has");
Check(SteamCmdOutput.ReadDepot("Depot download failed : Access Denied") is { State: ItemState.Failed, Reason: SteamCmdOutput.AnonymousRefused }, "a version download refused");
Check(SteamCmdOutput.ReadDepot("Downloading depot 494840 (10 files, 0 MB) ...") is null, "other depot lines are ignored");

Console.WriteLine("Log tail");
var logFile = Path.Combine(temp, "console_log.txt");
File.WriteAllText(logFile, "old line from an earlier run\n");
var tail = new LogTail(logFile);
File.AppendAllText(logFile, "first\nsec");
Check(tail.ReadNew().SequenceEqual(["first"]), "only new, complete lines");
File.AppendAllText(logFile, "ond\r\n\u001b[0mcoloured\u001b[0m\rprogress 50%\r");
Check(tail.ReadNew().SequenceEqual(["second", "coloured", "progress 50%"]), "joins split lines, strips colours, splits on carriage returns");
File.WriteAllText(logFile, "rotated\n");
Check(tail.ReadNew().SequenceEqual(["rotated"]), "starts again when the log is replaced");

Console.WriteLine("Workshop");
var ids = Workshop.ParseIds("<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=111&searchtext=\">x</a><a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=222\"></a><a href=\"/sharedfiles/filedetails/?id=111\"></a>").ToList();
Check(ids.SequenceEqual([111UL, 222UL, 111UL]), "item ids from a browse page");
var details = Workshop.ParseDetails("""
    {"response":{"result":1,"resultcount":4,"publishedfiledetails":[
      {"publishedfileid":"3815285055","result":1,"consumer_app_id":494840,"title":" Uboats: Coop Multiplayer ","description":"[h1]Hi[/h1]",
       "preview_url":"https://images.steamusercontent.com/ugc/1/A/","file_size":"7340032","time_created":1727000000,"time_updated":1759000000,
       "subscriptions":12850,"banned":0,"tags":[{"tag":"Gameplay"},{"tag":"2026.1"}],"hcontent_file":"18446744073709551000"},
      {"publishedfileid":"1","result":1,"consumer_app_id":294100,"title":"RimWorld mod"},
      {"publishedfileid":"2","result":9},
      {"publishedfileid":"3","result":1,"consumer_app_id":494840,"title":"Banned","banned":true}
    ]}}
    """);
Check(details.Count == 1, "keeps only live UBOAT items");
Check(details[0] is { Id: 3815285055, Title: "Uboats: Coop Multiplayer", Size: 7340032, Subscribers: 12850 } && details[0].Tags.SequenceEqual(["Gameplay", "2026.1"]), "reads fields given as numbers or strings");
Check(details[0].Updated == DateTimeOffset.FromUnixTimeSeconds(1759000000).UtcDateTime, "reads the update time");
Check(details[0].Manifest == 18446744073709551000, "reads the current version's manifest, even past the range of a long");
Check(Workshop.ParseDetails("""{"response":{"publishedfiledetails":[{"publishedfileid":"5","result":1,"consumer_app_id":494840}]}}""")[0].Manifest == 0, "no manifest given is 0");
Check(Workshop.ParseDetails("""{"response":{"result":1}}""").Count == 0, "an empty answer is no items, not a crash");
var cache = Path.Combine(temp, "workshop.json");
Workshop.Save(cache, details);
var loaded = Workshop.Load(cache).Single();
Check(loaded with { Tags = details[0].Tags } == details[0] && loaded.Tags.SequenceEqual(details[0].Tags), "the catalogue cache round-trips");
File.WriteAllText(cache, "{ not json");
Check(Workshop.Load(cache).Count == 0, "a damaged cache is ignored");

var acf = """
    "AppWorkshop"
    {
    	"appid"		"494840"
    	"WorkshopItemsInstalled"
    	{
    		"3815285055"
    		{
    			"size"		"7340032"
    			"timeupdated"		"1759000000"
    			"manifest"		"123"
    		}
    		"2114304535"
    		{
    			"size"		"1"
    			"TimeUpdated"		"1700000000"
    		}
    	}
    	"WorkshopItemDetails"
    	{
    		"3815285055"
    		{
    			"timeupdated"		"1760000000"
    		}
    		"999"
    		{
    			"timeupdated"		"1760000000"
    		}
    	}
    }
    """;
var versions = SteamCmd.Versions(acf);
Check(versions.Count == 2 && versions[3815285055] == DateTimeOffset.FromUnixTimeSeconds(1759000000).UtcDateTime, "the installed version of each SteamCMD download, ignoring the details block");
Check(SteamCmd.Versions("").Count == 0 && SteamCmd.Versions("\"AppWorkshop\" { }").Count == 0, "no record means no versions");
var manifests = SteamCmd.Manifests(acf);
Check(manifests.Count == 1 && manifests[3815285055] == 123, "the installed manifest of each item that has one");
File.WriteAllText(Path.Combine(temp, "old-cache.json"), """[{"Id":1,"Title":"Old","Description":"","PreviewUrl":"","Size":1,"Created":"2024-01-01T00:00:00Z","Updated":"2024-01-01T00:00:00Z","Subscribers":0,"Tags":[]}]""");
Check(Workshop.Load(Path.Combine(temp, "old-cache.json")).Single() is { Id: 1, Manifest: 0 }, "a catalogue cached before manifests were kept still loads");

Console.WriteLine("Mod lists");
PackEntry[] pack = [new("3815285055", 1063611784142694681, "Uboats: Coop Multiplayer"), new("uboat slots", 0, "Slots\twith a tab"), new("3746846377", 0, "Tiny")];
var exported = ModPack.Write(pack);
Check(exported.StartsWith('#') && exported.Contains("3815285055\t1063611784142694681\tUboats: Coop Multiplayer") && exported.Contains("3746846377\tlatest\tTiny"), "writes one line per mod with its version");
var imported = ModPack.Read(exported);
Check(imported.SequenceEqual([pack[0], pack[1] with { Name = "Slots with a tab" }, pack[2]]), "reads back what it wrote, in order");
Check(imported[0].WorkshopId == 3815285055 && imported[1].WorkshopId is null, "Workshop mods and local folders are told apart");
var handWritten = ModPack.Read("3815285055\n  3746846377 3024076959896569762  \r\n# note\n\n3815285055 99\n");
Check(handWritten.Count == 2 && handWritten[0] is { Manifest: 0, Name: "3815285055" } && handWritten[1].Manifest == 3024076959896569762, "a hand-written list of IDs works, and a repeat is ignored");

Console.WriteLine("Steam library");
var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
Check(SteamLibrary.LibraryPaths(vdf).SequenceEqual([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]), "library folders from libraryfolders.vdf");

Console.WriteLine("Manifests and folders");
string Folder(string path, string? manifest = null)
{
    Directory.CreateDirectory(path);
    if (manifest is not null) File.WriteAllText(Path.Combine(path, "Manifest.json"), manifest);
    return path;
}
var mods = Path.Combine(temp, "Mods");
var workshop = Path.Combine(temp, "workshop");
Folder(Path.Combine(mods, "uboatslots"), """{"name":"Slots","version":"1.2","supportedGameVersions":["2026.1"],"steamFileId":0}""");
Folder(Path.Combine(mods, "2114304535"), "\uFEFF{\"name\":\"UBE\",\"version\":\"5\",\"supportedGameVersions\":[\"2024.1 (Full Release)\\nPatch 14\",],} // trailing");
Folder(Path.Combine(workshop, "3815285055"), """{"Name":"Coop","minGameVersion":"2020.1","maxGameVersion":"2021.1","steamFileId":3815285055}""");
Folder(Path.Combine(workshop, "notanid"));
Folder(Path.Combine(mods, "broken"), "{ nope");
var found = ModFolder.Scan(mods, workshop).ToDictionary(m => m.Key);
Check(found.Keys.Order().SequenceEqual(["2114304535", "broken", "steam:3815285055", "uboatslots"]), "finds local mods, downloads and subscriptions");
Check(found["uboatslots"] is { Source: ModSource.Local, WorkshopId: null, Manifest.Version: "1.2" }, "a local mod");
Check(found["2114304535"] is { Source: ModSource.Download, WorkshopId: 2114304535 } && found["2114304535"].Manifest!.GameVersions.SequenceEqual(["2024.1 (Full Release)"]), "a download, with a lenient manifest");
Check(found["steam:3815285055"] is { Source: ModSource.Steam, WorkshopId: 3815285055, Manifest.Name: "Coop" } && found["steam:3815285055"].Manifest!.GameVersions.SequenceEqual(["2020.1", "2021.1"]), "a subscription with an old-style manifest");
Check(found["broken"].Manifest is null, "an unreadable manifest is skipped");
Check(ModFolder.Missing("steam:42").WorkshopId == 42UL && ModFolder.Scan(Path.Combine(temp, "nowhere"), null).Any() == false, "missing entries and folders");

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("SteamCMD run (fake steamcmd)");
    var root = Path.Combine(temp, "steamcmd");
    Directory.CreateDirectory(root);
    var fake = Path.Combine(root, "steamcmd.exe");

    void Fake(string body)
    {
        File.WriteAllText(fake, "#!/bin/bash\nroot=\"$(dirname \"$0\")\"\nmkdir -p \"$root/logs\"\nlog=\"$root/logs/console_log.txt\"\nscript=\"$2\"\n" + body);
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    Fake("""
        echo "Redirecting stderr to '$root/logs/stderr.txt'"
        for id in $(grep -o 'workshop_download_item 494840 [0-9]*' "$script" | awk '{print $3}'); do
          case $id in
            111) echo "[t] Downloading item 111 ..." >> "$log"
                 mkdir -p "$root/steamapps/workshop/downloads/494840/111"
                 head -c 100 /dev/zero > "$root/steamapps/workshop/downloads/494840/111/part"
                 sleep 0.8
                 echo "Success. Downloaded item 111 to \"$root/steamapps/workshop/content/494840/111\" (200 bytes)" ;;
            222) echo "ERROR! Download item 222 failed (Failure)." >> "$log" ;;
            333) echo "ERROR! Timeout downloading item 333" >> "$log" ;;
          esac
        done
        exit 6
        """);
    var events = new ConcurrentQueue<SteamCmdEvent>();
    var steam = new SteamCmd(root);
    var results = await steam.DownloadAsync([(111, 200), (222, 50), (333, 50), (444, 50)], new Progress<SteamCmdEvent>(events.Enqueue));
    await Task.Delay(200);
    var script = File.ReadAllLines(Path.Combine(root, "flotilla.txt"));
    Check(script.Contains($"force_install_dir \"{root}\"") && script.Contains("login anonymous") && script.Contains("workshop_download_item 494840 444 validate") && script[^1] == "quit", "writes the SteamCMD script");
    Check(results[111] is null, "a success from the pipe counts");
    Check(results[222] == "Steam reported a failure" && results[333] == "the download timed out", "failures from the log count, with a reason");
    Check(results[444] == "SteamCMD stopped before downloading it", "an item SteamCMD never mentioned is a failure");
    Check(events.Any(e => e is { Id: 111, State: ItemState.Downloading, Progress: 0.5 }), "progress from the download folder's size");

    Fake("""
        echo "ERROR! Not logged on." >> "$log"
        """);
    results = await steam.DownloadAsync([(5, 1), (6, 1)], new Progress<SteamCmdEvent>(_ => { }));
    Check(results.Values.All(r => r == SteamCmdOutput.AnonymousRefused), "a refused login fails the whole batch with the reason");

    Fake("""
        echo "[t] Downloading item 7 ..." >> "$log"
        sleep 30
        """);
    var watch = Stopwatch.StartNew();
    results = await new SteamCmd(root) { StallAfter = TimeSpan.FromSeconds(1.5) }.DownloadAsync([(7, 1)], new Progress<SteamCmdEvent>(_ => { }));
    Check(watch.Elapsed < TimeSpan.FromSeconds(10) && results[7] == "SteamCMD stopped responding", $"a stalled SteamCMD is stopped ({watch.Elapsed.TotalSeconds:0.0} s)");
}

Directory.Delete(temp, recursive: true);
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
