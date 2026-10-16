using Flotilla;
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

Console.WriteLine("Profiles");
var (profileOn, profileMissing) = ProfileStore.Resolve(["steam:1", "2", "Local Mod"], ["2", "local mod", "3", "steam:1"]);
Check(profileOn.SequenceEqual(["2", "Local Mod", "steam:1"]) && profileMissing.SequenceEqual(["3"]), "a profile's mods are found in its order, and missing ones are listed");
Check(ProfileStore.Resolve(["steam:7"], ["7"]).On.SequenceEqual(["steam:7"]), "a mod installed by Flotilla matches the same mod subscribed in Steam");
Check(ProfileStore.Resolve(["7"], ["steam:7"]).On.SequenceEqual(["7"]), "a mod subscribed in Steam matches the same mod installed by Flotilla");
Check(ProfileStore.Resolve(["7", "steam:7"], ["7", "steam:7"]).On.SequenceEqual(["7", "steam:7"]), "both copies of a mod can be in a profile");
var profile = new Profile("Overhaul", ["a", "b"]);
Check(ProfileStore.IsActive(profile, ["a", "b", "c"], ["a", "b"]), "a profile is active when exactly its mods are on, in its order");
Check(!ProfileStore.IsActive(profile, ["a", "b", "c"], ["b", "a"]), "a different order isn't the profile");
Check(!ProfileStore.IsActive(profile, ["a", "b", "c"], ["a", "b", "c"]), "an extra mod switched on isn't the profile");
Check(!ProfileStore.IsActive(profile, ["a"], ["a"]), "a profile with a missing mod isn't active");
var profilesFile = Path.Combine(temp, "profiles.json");
ProfileStore.Save(profilesFile, [profile, new Profile("Vanilla", [])]);
var savedProfiles = ProfileStore.Load(profilesFile);
Check(savedProfiles.Count == 2 && savedProfiles[0].Name == "Overhaul" && savedProfiles[0].Mods.SequenceEqual(["a", "b"]) && savedProfiles[1].Mods.Count == 0, "profiles are saved and read back");
File.WriteAllText(profilesFile, "not json");
Check(ProfileStore.Load(profilesFile).Count == 0, "a broken profiles file means no profiles, not a crash");
Check(ProfileStore.Load(Path.Combine(temp, "none.json")).Count == 0, "no profiles file means no profiles");

Console.WriteLine("Requirements");
var page = """
    <div class="requiredItemsContainer" id="RequiredItems"> <a href="https://steamcommunity.com/workshop/filedetails/?id=2898164268" target="_blank"> <div class="requiredItem"> [2026.1] Shared Bundles </div> </a>
    <a href="https://steamcommunity.com/workshop/filedetails/?id=2892964674"> <div class="requiredItem"> German Aircraft </div> </a> </div> </div> <!-- created by -->
    <a href="https://steamcommunity.com/sharedfiles/filedetails/?id=999">Another item further down</a>
    """;
Check(Requirements.Parse(page).SequenceEqual([2898164268UL, 2892964674UL]), "required items from a Workshop page, and nothing past them");
Check(Requirements.Parse("<a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=5\">x</a>").Count == 0, "a page without required items needs nothing");
var requirementsFile = Path.Combine(temp, "requirements.json");
Requirements.Save(requirementsFile, new() { [7] = new([1, 2], new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)) });
Check(Requirements.Load(requirementsFile)[7].Ids.SequenceEqual([1UL, 2UL]), "required items are cached and read back");

Console.WriteLine("Game log");
var fingerprints = new ModFingerprints(
    new() { ["PinguMP"] = "steam:3815285055", ["NoseDown.Patches"] = "nosedown", ["UBOAT"] = "nobody" },
    new() { ["PinguMP"] = "steam:3815285055", ["Nose Down"] = "nosedown" });
var log = GameLog.Parse([
    @"C:\Users\x\AppData\LocalLow\Deep Water Studio\UBOAT\Mods\3815285055\Source\MpMenuTab.Language.cs(31,23): error CS1501: No overload for method 'OpenPage' takes 2 arguments",
    @"C:\Users\x\AppData\LocalLow\Deep Water Studio\UBOAT\Mods\3815285055\Source\NzoUniformCompat.cs(807,13): warning CS0162: Unreachable code detected",
    @"C:\Users\x\AppData\LocalLow\Deep Water Studio\UBOAT\Mods\3815285055\Source\MpMenuTab.Language.cs(31,23): error CS1501: No overload for method 'OpenPage' takes 2 arguments",
    "Game version: 2026.1 Patch 21",
    @"D:\SteamLibrary\steamapps\workshop\content\494840\2114304535\Source\Patch.cs(5,1): error CS0246: The type 'Foo' could not be found",
    "NullReferenceException: Object reference not set to an instance of an object",
    "  at UBOAT.Game.Scene.Entities.Ship.Update () [0x00012] in <abc>:0",
    "",
    "ArgumentOutOfRangeException: Index was out of range.",
    "  at System.Collections.Generic.List`1[T].get_Item (System.Int32 index) [0x00009] in <abc>:0",
    "  at NoseDown.Patches.TrimPatch.Postfix (UBOAT.Game.Scene.Entities.Ship __instance) [0x0001c] in <def>:0",
    "  at (wrapper dynamic-method) UBOAT.Game.Scene.Entities.Ship.DMD<Update>(UBOAT.Game.Scene.Entities.Ship)",
    "[PinguMP] ERROR: Build check: OLD CACHED BUILD: the mod folder holds 1.11.5",
    "[PinguMP] WARN: Face render failed for 8df2#Cap",
    "[PinguMP] Session stopped (Joining): Protocol mismatch with the host.",
    "[PinguMP] HEARTBEAT Joined OwnBoats | fps 109.6 | clock error 0.00 s, waves 0.00 s",
    "[PinguMP] Settings loaded: errors shown in chat = true",
    @"[NoseDown] config loaded from C:/Users/x/AppData/LocalLow/Deep Water Studio/UBOAT\NoseDownConfig.txt",
    "Could not load file or assembly 'SomeLib, Version=1.0.0.0' or one of its dependencies.",
], fingerprints);
Check(log.GameVersion == "2026.1 Patch 21", "reads the game version");
Check(log.Errors("3815285055").Select(p => p.Summary).SequenceEqual(["it failed to compile (CS1501: No overload for method 'OpenPage' takes 2 arguments)"]), "a compile error is pinned on its mod once, and compiler warnings are ignored");
Check(log.Errors("steam:2114304535").Any(), "an error in a Steam subscription is pinned on it");
Check(log.Errors("nosedown").Single() is { Summary: "ArgumentOutOfRangeException: Index was out of range.", Lines.Count: 4 }, "an exception is pinned on the mod whose code is in its stack trace, with the trace kept");
Check(log.Errors("steam:3815285055").Single().Summary.Contains("OLD CACHED BUILD"), "a mod's own ERROR line is pinned on it by its log tag");
Check(log.ByMod["steam:3815285055"].Count(p => p.Severity == Severity.Warning) == 1, "a mod's WARN line is kept as a warning, not an error");
Check(log.Unattributed.Count == 2 && log.Unattributed.All(p => p.Severity == Severity.Error), "a game exception and a load failure that name no mod are kept apart");
Check(!log.ByMod.ContainsKey("nobody"), "the game's own namespace never blames a mod");
Check(log.Report(new Dictionary<string, string> { ["nosedown"] = "Nose Down" }, DateTime.UtcNow).Contains("== Nose Down (nosedown): 1 errors"), "the saved report names each mod");
Check(GameLog.Supports(["2026.1"], "2026.1 Patch 21") && GameLog.Supports(["2026.1 patch 18", "2026.1 Patch 21"], "2026.1 Patch 21"), "a mod for this release or this exact patch is fine");
Check(!GameLog.Supports(["2024.1 (Full Release)"], "2026.1 Patch 21") && !GameLog.Supports(["2026.1 Patch 15"], "2026.1 Patch 21"), "a mod only for another release or patch is flagged");
Check(GameLog.Supports([], "2026.1 Patch 21"), "a mod that names no version isn't flagged");
Check(GameLog.SplitTitle("[2024.1] Enhanced Identification Book") == ("Enhanced Identification Book", "2024.1")
    && GameLog.SplitTitle("[2026.1 Patch 21] Nose Down") == ("Nose Down", "2026.1"), "a game version at the start of a title is split off");
Check(GameLog.SplitTitle("Uboats: Coop Multiplayer") == ("Uboats: Coop Multiplayer", null)
    && GameLog.SplitTitle("[WIP] Nose Down") == ("[WIP] Nose Down", null) && GameLog.SplitTitle("[2026.1]") == ("[2026.1]", null), "other titles are left alone");
Console.WriteLine("Mod settings");
var txtSettings = SettingsFile.FromLines("NoseDownConfig.txt", "# Nose Down settings.\r\n# Edit a value.\r\n\r\n# Multiplier on every trim angle.\r\n# Allowed range 0.25 to 2.0.\r\nangleScale=1\r\n\r\n# First-person view leans.\r\nenableFPPTilt=true\r\n\r\nenabled=True\r\nname = Kaleun\r\n")!;
Check(txtSettings.About == "Nose Down settings. Edit a value." && txtSettings.Fields.Count == 4, "a key=value file's heading and settings are read");
Check(txtSettings.Fields[0] is { Key: "angleScale", Label: "Angle scale", Kind: FieldKind.Number, Description: "Multiplier on every trim angle. Allowed range 0.25 to 2.0." }, "the comment above a setting describes it");
Check(txtSettings.Fields[1].Kind == FieldKind.Switch && txtSettings.Fields[1].Label == "Enable fpp tilt" && txtSettings.Fields[3] is { Key: "name", Value: "Kaleun", Kind: FieldKind.Text }, "switches and text are told apart");
txtSettings.Fields[0].Value = "1.5";
txtSettings.Fields[2].IsOn = false;
var txtOriginal = "# Nose Down settings.\r\n# Edit a value.\r\n\r\n# Multiplier on every trim angle.\r\n# Allowed range 0.25 to 2.0.\r\nangleScale=1\r\n\r\n# First-person view leans.\r\nenableFPPTilt=true\r\n\r\nenabled=True\r\nname = Kaleun\r\n";
Check(txtSettings.Render(txtOriginal) == txtOriginal.Replace("angleScale=1", "angleScale=1.5").Replace("enabled=True", "enabled=False"), "saving changes only the edited values, keeping comments, casing and line endings");
txtSettings.Fields[0].Value = "lots";
Check(txtSettings.Problem(txtSettings.Fields[0]) is not null, "a number setting refuses text");
var jsonSettings = SettingsFile.FromJson("living-depths-config.json", """{ "density": "medium", "renderDistance": 120.0, "verboseLogging": false }""")!;
Check(jsonSettings.Fields.Select(f => f.Kind).SequenceEqual([FieldKind.Text, FieldKind.Number, FieldKind.Switch]), "a JSON file's settings are read with their types");
jsonSettings.Fields[1].Value = "200";
jsonSettings.Fields[2].IsOn = true;
var jsonSaved = System.Text.Json.Nodes.JsonNode.Parse(jsonSettings.Render("""{ "density": "medium", "renderDistance": 120.0, "verboseLogging": false }"""))!;
Check(jsonSaved["renderDistance"]!.GetValue<double>() == 200 && jsonSaved["verboseLogging"]!.GetValue<bool>() && jsonSaved["density"]!.GetValue<string>() == "medium", "saving JSON keeps numbers as numbers and switches as true or false");

Console.WriteLine("History, bisect and share codes");
PackEntry Entry(string key, ulong manifest = 0) => new(key, manifest, key.ToUpperInvariant());
var then = new List<PackEntry> { Entry("a", 1), Entry("b"), Entry("c") };
Check(History.Compare(then, [Entry("a", 1), Entry("b"), Entry("c")]).Same, "the same setup is the same");
var difference = History.Compare(then, [Entry("c"), Entry("a", 2), Entry("d")]);
Check(difference is { Added: ["D"], Removed: ["B"], Versions: ["A"], Reordered: true } && difference.Describe() == "1 more on, 1 fewer on, 1 at another version, a different order", "a changed setup is described");
var snapshots = new List<Snapshot>();
var start = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
History.Record(snapshots, new Snapshot(start, "Switched mods", then));
Check(!History.Record(snapshots, new Snapshot(start.AddMinutes(5), "Switched mods", then)), "an unchanged setup isn't recorded twice");
History.Record(snapshots, new Snapshot(start.AddMinutes(1), "Switched mods", [Entry("a", 1)]));
Check(snapshots.Count == 1 && snapshots[0].Mods.Count == 1, "quick changes for the same reason are merged into one snapshot");
History.Record(snapshots, new Snapshot(start.AddMinutes(10), "Updated mods", [Entry("a", 2)]));
Check(snapshots.Count == 2 && snapshots[0].Reason == "Updated mods", "the newest snapshot comes first");

var setup = Enumerable.Range(1, 8).Select(i => Entry($"m{i}")).ToList();
IEnumerable<string> Needs(string key) => key == "m2" ? ["m8"] : [];
var bisect = Bisect.Start(setup, [.. setup.Select(m => m.Key)], Needs);
Check(bisect.Testing.SequenceEqual(["m1", "m2", "m3", "m4", "m8"]) && bisect.RoundsLeft == 3, "the first round tests half the mods, plus anything they need");
Check(bisect.EnabledNow().SequenceEqual(["m1", "m2", "m3", "m4", "m8"]), "only the half being tested is switched on");
bisect = bisect.Answer(stillBroken: false, Needs);
Check(bisect.Suspects.SequenceEqual(["m5", "m6", "m7"]), "a test that works clears the mods that were on");
bisect = bisect.Answer(stillBroken: true, Needs);
Check(bisect.Suspects.SequenceEqual(["m5", "m6"]), "a test that's still broken keeps the mods that were on");
bisect = bisect.Answer(stillBroken: true, Needs);
Check(bisect.Culprit == "m5" && bisect.Round == 4, "the search ends on a single mod");
Check(ShareCodes.Normalise("flt-8k2qrt") == "8K2QRT" && ShareCodes.Normalise("FLT-8K2QR7") == "8K2QR7" && ShareCodes.Display("8K2QR7") == "FLT-8K2QR7", "share codes ignore case and the FLT- prefix");
Check(ShareCodes.Normalise("flotilla://setup/8K2QR7") == "8K2QR7" && ShareCodes.Normalise("8K2-QR7") == "8K2QR7", "a link or a code with a dash works too");
Check(ShareCodes.Normalise("OOOOOO") is null && ShareCodes.Normalise("8K2Q") is null, "look-alike letters and short codes are refused");
Check(Community.ParseShare("""{"code":"8K2QR7","expires":1760612400}""") == new SharedCode("8K2QR7", new DateTime(2025, 10, 16, 11, 0, 0, DateTimeKind.Utc))
    && Community.ParseShare("""{"code":"8K2QR7"}""") == new SharedCode("8K2QR7", null) && Community.ParseShare("""{"error":"Try again"}""") is null, "a share code is read with its expiry, when the server sends one");
Check(Community.ParseVotes("""{"mods":{"3815285055":[12,1],"bad":[1,1],"7":[0,0]}}""") is { Count: 2 } votes && votes[3815285055] is { Percent: 92, Total: 13 }, "vote counts are read");

Console.WriteLine("Change notes");
var notes = Workshop.ParseChangeNotes("""<div class="changelog headline"> Update: 3 Jul @ 4:12pm </div> <p id="1783088739">Sounds Fix , IX Crew size fixes<br>Updated Manifest &amp; Locales</p> <p id="1783080076">Revert <br></p>""");
Check(notes.Count == 2 && notes[0].Text == "Sounds Fix , IX Crew size fixes\nUpdated Manifest & Locales" && notes[0].Time == DateTimeOffset.FromUnixTimeSeconds(1783088739).UtcDateTime, "change notes are read with their dates and line breaks");

Console.WriteLine("Updates");
Check(Updater.IsNewer("0.1.3", "0.1.2") && Updater.IsNewer("0.2", "0.1.9") && Updater.IsNewer("1.0.0", "0.9.9"), "a higher version is newer");
Check(Updater.IsNewer("0.1.1b", "0.1.1") && Updater.IsNewer("0.1.2", "0.1.1b") && !Updater.IsNewer("0.1.1b", "0.1.2"), "a lettered hotfix sits between versions");
Check(!Updater.IsNewer("0.1.2", "0.1.2") && !Updater.IsNewer("0.1.1", "0.1.2") && !Updater.IsNewer("0.1.10", "0.1.10"), "the same or an older version isn't newer");
Check(Updater.IsNewer("0.1.10", "0.1.9"), "versions compare as numbers, not text");
Check(Updater.VersionIn("V0.1.2") == "0.1.2" && Updater.VersionIn("v0.1.1b") == "0.1.1b" && Updater.VersionIn("Flotilla 1.4 beta") == "1.4" && Updater.VersionIn("V012") is null, "the version in a release name");
var release = Updater.Parse("""
    {"tag_name":"V013","name":"V0.1.3","draft":false,"prerelease":false,"html_url":"https://github.com/Harry149/Flotilla/releases/tag/V013",
     "body":"- Mod authors\r\n","assets":[
       {"name":"notes.txt","size":3,"browser_download_url":"https://x/notes.txt"},
       {"name":"Flotilla.exe","size":140067886,"digest":"sha256:cd105244c91c3d8cebe424ef04d736815cdfafe347ee7ab0eb585a78eb725e3a","browser_download_url":"https://x/Flotilla.exe"}]}
    """);
Check(release is { Version: "0.1.3", Notes: "- Mod authors", Size: 140067886, Download: "https://x/Flotilla.exe", Sha256: "cd105244c91c3d8cebe424ef04d736815cdfafe347ee7ab0eb585a78eb725e3a" }, "reads the latest release and its Flotilla.exe");
Check(Updater.Parse("""{"name":"V0.2.0","prerelease":true,"assets":[{"name":"Flotilla.exe","size":1}]}""") is null, "a pre-release isn't offered");
Check(Updater.Parse("""{"name":"V0.2.0","assets":[{"name":"Other.zip","size":1}]}""") is null, "a release without Flotilla.exe isn't offered");
Check(Updater.Parse("""{"name":"V0.2.0","assets":[{"name":"Flotilla.exe","size":1}]}""") is { Sha256: null }, "a release without a checksum still works");
Check(Updater.CountDownloads("""[{"assets":[{"name":"Flotilla.exe","download_count":2},{"name":"notes.txt","download_count":50}]},{"assets":[{"name":"Flotilla.exe","download_count":3}]},{"assets":[]}]""") == 5, "downloads of Flotilla.exe across every release are added up");

var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var scrubbed = ErrorReports.Scrub($@"IOException at {profilePath}\AppData\Local\Flotilla\order.txt");
Check(!scrubbed.Contains(profilePath, StringComparison.OrdinalIgnoreCase) && scrubbed.Contains(@"%USERPROFILE%\AppData"), "error reports don't contain the user's folder or name");
Check(ErrorReports.Scrub(new string('x', 100_000)).Length == 60_000, "an error report is capped in size");
Check(Sailors.Parse("""{"sailors":42}""") == 42 && Sailors.Parse("""{"error":"Not found"}""") is null, "reads the sailor count");

Console.WriteLine("Featured");
Check(Flotilla.Featured.Parse("# notes\r\n3814892328\n  3744719470  # Living Depths\n\nnot an id\n3814892328\n").SequenceEqual([3814892328UL, 3744719470UL]), "reads IDs in order, skipping notes, blanks, junk and repeats");
Check(Flotilla.Featured.Parse("").Count == 0, "an empty file features nothing");

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
var video = BBCode.Parse("[previewyoutube=8I__6lKNi5w;full][/previewyoutube]");
Check(video.Children.Count == 1 && BBCode.YouTubeId(video.Children[0]) == "8I__6lKNi5w" && video.PlainText == "", "a YouTube embed becomes a video, not text");
Check(BBCode.YouTubeId(BBCode.Parse("[previewyoutube=\"><script>;full][/previewyoutube]").Children[0]) is null, "a malformed video ID is ignored");
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
       "subscriptions":12850,"banned":0,"tags":[{"tag":"Gameplay"},{"tag":"2026.1"}],"hcontent_file":"18446744073709551000","creator":"76561198023548424"},
      {"publishedfileid":"1","result":1,"consumer_app_id":294100,"title":"RimWorld mod"},
      {"publishedfileid":"2","result":9},
      {"publishedfileid":"3","result":1,"consumer_app_id":494840,"title":"Banned","banned":true}
    ]}}
    """);
Check(details.Count == 1, "keeps only live UBOAT items");
Check(details[0] is { Id: 3815285055, Title: "Uboats: Coop Multiplayer", Size: 7340032, Subscribers: 12850 } && details[0].Tags.SequenceEqual(["Gameplay", "2026.1"]), "reads fields given as numbers or strings");
Check(details[0].Updated == DateTimeOffset.FromUnixTimeSeconds(1759000000).UtcDateTime, "reads the update time");
Check(details[0].Manifest == 18446744073709551000, "reads the current version's manifest, even past the range of a long");
Check(details[0].Creator == 76561198023548424, "reads the author's Steam ID");
Check(Workshop.ParseDetails("""{"response":{"publishedfiledetails":[{"publishedfileid":"5","result":1,"consumer_app_id":494840}]}}""")[0] is { Manifest: 0, Creator: 0 }, "no manifest or author given is 0");
Check(SteamProfiles.ParseName("<profile><steamID64>7656</steamID64>\n<steamID><![CDATA[Pingu]]></steamID></profile>") == "Pingu", "an author's name from their profile");
Check(SteamProfiles.ParseName("<steamID>Tom &amp; Jerry</steamID>") == "Tom & Jerry", "an author's name without CDATA is decoded");
Check(SteamProfiles.ParseName("<response><error>The specified profile could not be found.</error></response>") is null, "a missing profile has no name");
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

Console.WriteLine("Best load order");
HashSet<string> Sheets(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
OrderMod Mod(string key, string name, ulong? id = null, ulong[]? needs = null, HashSet<string>? sheets = null, bool text = false, string about = "") =>
    new(key, name, about, id, needs ?? [], sheets ?? Sheets(), text);
var (best, why) = LoadOrder.Best([Mod("a", "Sea Spray Plus", 2, needs: [1]), Mod("b", "Hull Pitch", 1)]);
Check(best.SequenceEqual(["b", "a"]) && why["b"] == "Sea Spray Plus needs it", "a mod loads after what it needs");
(best, why) = LoadOrder.Best([Mod("p", "[2026.1] Nose Down Patch"), Mod("x", "Shadow Zone"), Mod("n", "[2026.1] Nose Down")]);
Check(best.SequenceEqual(["x", "n", "p"]) && why["p"] == "Made for Nose Down, so it loads after it", "a patch loads after the mod it's for");
(best, _) = LoadOrder.Best([Mod("c", "Compat fix", about: "Makes Shadow Zone work with Nose Down"), Mod("s", "Shadow Zone")]);
Check(best.SequenceEqual(["s", "c"]), "a compatibility mod loads after a mod its description names");
(best, why) = LoadOrder.Best([Mod("t", "German Text", sheets: Sheets("Locales.xlsx"), text: true), Mod("g", "Gameplay")]);
Check(best.SequenceEqual(["g", "t"]) && why["t"] == "Only changes text, so it loads last", "text-only mods load last");
(best, _) = LoadOrder.Best([Mod("small", "Crew Names", sheets: Sheets("Crew.xlsx")), Mod("big", "Overhaul", sheets: Sheets("Crew.xlsx", "Ships.xlsx", "Weapons.xlsx"))]);
Check(best.SequenceEqual(["big", "small"]), "a mod changing more data sheets loads before a smaller one sharing a sheet");
(best, _) = LoadOrder.Best([Mod("x", "One", sheets: Sheets("Crew.xlsx")), Mod("y", "Two", sheets: Sheets("Ships.xlsx", "Guns.xlsx")), Mod("z", "Three")]);
Check(best.SequenceEqual(["x", "y", "z"]), "mods no rule covers keep their order");
(best, _) = LoadOrder.Best([Mod("a", "Alpha", 1, needs: [2]), Mod("b", "Bravo", 2, needs: [1])]);
Check(best.Count == 2 && best.ToHashSet().SetEquals(["a", "b"]), "mods that need each other don't break the sort");
(best, _) = LoadOrder.Best([Mod("small", "Crew Names", 1, sheets: Sheets("Crew.xlsx")), Mod("big", "Overhaul", 2, needs: [1], sheets: Sheets("Crew.xlsx", "Ships.xlsx"))]);
Check(best.SequenceEqual(["small", "big"]), "what a mod needs beats the data sheet rule");

Console.WriteLine("Steam's load order");
var subscriptions = "\"subscribedfiles\"\n{\n\t\"appid\"\t\t\"494840\"\n\t\"time_last_updated\"\t\t\"1791999816\"\n"
    + "\t\"0\"\n\t{\n\t\t\"publishedfileid\"\t\t\"111\"\n\t\t\"time_subscribed\"\t\t\"1\"\n\t\t\"disabled_locally\"\t\t\"0\"\n\t}\n"
    + "\t\"1\"\n\t{\n\t\t\"publishedfileid\"\t\t\"222\"\n\t\t\"time_subscribed\"\t\t\"2\"\n\t\t\"disabled_locally\"\t\t\"0\"\n\t}\n"
    + "\t\"2\"\n\t{\n\t\t\"publishedfileid\"\t\t\"333\"\n\t\t\"time_subscribed\"\t\t\"3\"\n\t\t\"disabled_locally\"\t\t\"1\"\n\t}\n}\n";
var arranged = SteamOrder.Arrange(subscriptions, [(333, true), (111, false)]);
string[] SteamIds(string text) => [.. System.Text.RegularExpressions.Regex.Matches(text, "\"publishedfileid\"\\s*\"(\\d+)\"").Select(m => m.Groups[1].Value)];
Check(SteamIds(arranged).SequenceEqual(["333", "111", "222"]), "Steam's order follows Flotilla's, and mods Flotilla doesn't list go last");
Check(arranged.Contains("\t\"0\"\n\t{\n\t\t\"publishedfileid\"\t\t\"333\"") && arranged.Contains("\t\"2\"\n\t{\n\t\t\"publishedfileid\"\t\t\"222\"")
    && arranged.StartsWith("\"subscribedfiles\"\n{\n\t\"appid\"") && arranged.EndsWith("\t}\n}\n"), "entries are renumbered and the rest of the file is kept");
Check(arranged.Contains("\"333\"\n\t\t\"time_subscribed\"\t\t\"3\"\n\t\t\"disabled_locally\"\t\t\"0\"")
    && arranged.Contains("\"111\"\n\t\t\"time_subscribed\"\t\t\"1\"\n\t\t\"disabled_locally\"\t\t\"1\"")
    && arranged.Contains("\"222\"\n\t\t\"time_subscribed\"\t\t\"2\"\n\t\t\"disabled_locally\"\t\t\"0\""), "Steam's tick boxes match Flotilla's switches, and unknown mods keep theirs");
Check(SteamOrder.Arrange(arranged, [(333, true), (111, false)]) == arranged, "an order that already matches leaves the file unchanged");

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

if (OperatingSystem.IsWindows())
{
    Console.WriteLine("SteamCMD's download folder");
    var steamRoot = Path.Combine(temp, "steamcmd-link");
    var firstMods = Path.Combine(temp, "first", "Mods");
    var secondMods = Path.Combine(temp, "second", "Mods");
    Directory.CreateDirectory(steamRoot);
    var linked = new SteamCmd(steamRoot) { Executable = Environment.ProcessPath! };
    await linked.PrepareAsync(firstMods, new Progress<string>(_ => { }));
    await linked.PrepareAsync(secondMods, new Progress<string>(_ => { }));
    var content = Path.Combine(steamRoot, "steamapps", "workshop", "content", "494840");
    File.WriteAllText(Path.Combine(content, "probe.txt"), "x");
    Check(File.Exists(Path.Combine(secondMods, "probe.txt")) && !File.Exists(Path.Combine(firstMods, "probe.txt")), "downloads follow a new UBOAT data folder");
    Directory.Delete(content);
}

Console.WriteLine("Rolling back");
{
    var mod = Path.Combine(temp, "rollback", "123");
    var backup = Path.Combine(temp, "rollback-backup", "files");
    Directory.CreateDirectory(Path.Combine(mod, "Data"));
    File.WriteAllText(Path.Combine(mod, "Data", "v.txt"), "new");
    Directory.CreateDirectory(Path.Combine(backup, "Data"));
    File.WriteAllText(Path.Combine(backup, "Data", "v.txt"), "old");
    Folders.Replace(mod, backup);
    Check(File.ReadAllText(Path.Combine(mod, "Data", "v.txt")) == "old" && !Directory.Exists(mod + ".newer"), "the backup takes the mod's place");

    try
    {
        Folders.Replace(mod, Path.Combine(temp, "no such backup"));
        Check(false, "a missing backup fails");
    }
    catch (IOException)
    {
        Check(File.ReadAllText(Path.Combine(mod, "Data", "v.txt")) == "old" && !Directory.Exists(mod + ".newer"), "a failed swap puts the mod back where it was");
    }
}

Directory.Delete(temp, recursive: true);
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
