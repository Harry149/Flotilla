# Flotilla

A mod manager for UBOAT. Browse the Steam Workshop, install and update mods without Steam, switch them on and off, and set their load order.

## Build and run

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), or Visual Studio 2026 with the .NET desktop workload.

```
dotnet run --project src/Flotilla
```

To make a single `Flotilla.exe` you can hand to someone:

```
dotnet publish src/Flotilla -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

The tests run on Windows, macOS or Linux:

```
dotnet run --project tests/Flotilla.Tests
```

## How it works

**The Workshop list** comes from Steam's public browse pages and the `GetPublishedFileDetails` web API, so no API key is needed. It's cached in `%LOCALAPPDATA%\Flotilla\workshop.json`, so the list opens straight away next time and refreshes in the background.

**Installing** uses SteamCMD with an anonymous login. Flotilla downloads SteamCMD into `%LOCALAPPDATA%\Flotilla\steamcmd` the first time you install something and always runs it hidden. Its download folder is linked (a directory junction) to UBOAT's own mods folder, so each mod lands in `%USERPROFILE%\AppData\LocalLow\Deep Water Studio\UBOAT\Mods\<workshop id>`. A failed download is retried up to three times. If SteamCMD goes quiet for five minutes it is stopped.

**Updates** come from the same place. SteamCMD keeps a record of the version it downloaded for each mod (`steamapps\workshop\appworkshop_494840.acf` in Flotilla's SteamCMD folder). When the Workshop has something newer, the mod gets an Update button on its page and on the Installed list, and Update all fetches every one of them. Subscriptions made in Steam are kept up to date by Steam, so Flotilla leaves those alone.

**Switching mods on and off, and the load order**, both live in UBOAT's own `modlist.txt`, next to the Mods folder. One line per enabled mod, top to bottom:

- `steam:<id>` for a mod you subscribed to in Steam
- the folder name for anything in the Mods folder, including mods Flotilla installed

Flotilla rewrites that file whenever you change something, keeping its line endings. Before the first change it keeps a copy as `modlist.txt.bak`. Disabled mods aren't in `modlist.txt` at all, so Flotilla remembers where they sat in `%LOCALAPPDATA%\Flotilla\order.txt`. Turning one back on puts it back in the same place. If you change mods in UBOAT's launcher, Flotilla picks that up the next time its window gets focus.

**Steam's load order** follows Flotilla's too. Steam keeps the order and tick box of each Workshop subscription in `userdata\<account>\ugc\494840_subscriptions.vdf` in its folder, with "0" loading first. Flotilla rewrites that file whenever it saves, putting the subscriptions in its own order and ticking only the ones switched on. Steam only reads the file when it starts, so if Steam was already open when Flotilla last changed it, Play UBOAT closes Steam first and the game starts it again. Switch this off in Settings with Keep Steam's load order the same as Flotilla's.

**Export and Import** share a setup. Export saves a text file listing every mod that's switched on, in load order, one per line: the Workshop ID, the exact version (the Steam manifest ID, or `latest` when Flotilla doesn't know it), and the name. Import reads that file and installs each mod at that version: mods wanted at their latest version download 25 to a SteamCMD run (failed ones get one more try), and mods pinned to an older version download one at a time with SteamCMD's `download_depot`. If Steam no longer has that version, Import installs the latest one and says so. It then switches on exactly the mods in the file, in its order. Mods that aren't in the file stay installed, switched off. A mod you subscribe to in Steam is used as it is, because Steam decides its version. A local mod is only switched on if a folder with that name is already in the Mods folder. Flotilla records the versions it installed this way in `steamcmd\versions.txt`, so the Update button still knows when the Workshop has something newer.

**Profiles** save which mods are switched on and their load order, under a name, in `%LOCALAPPDATA%\Flotilla\profiles.json`. Press + next to Profiles in the sidebar to save the current setup, and click a profile to switch to it: its mods are switched on in its order and everything else is switched off. A Workshop mod in the profile that isn't installed is installed first and slotted into place. A mod saved from Flotilla matches the same mod subscribed in Steam, and the other way round. Right-click a profile to update it with the mods that are on now, rename, reorder or delete it.

**Required mods** come from the "Required items" list on each mod's Workshop page, cached for three days in `%LOCALAPPDATA%\Flotilla\requirements.json`. Installing a mod installs what it needs and loads those first. On Installed, a mod whose requirement is missing, switched off or loads after it gets a yellow note and a Fix button. A mod's page lists what it needs. A mod that's switched on both as a Steam subscription and as a Flotilla download gets a note too, and Fix switches the Flotilla copy off.

**Version and error warnings.** UBOAT writes its version (`Game version: 2026.1 Patch 21`) at the top of `output_log.txt`. A mod whose manifest names neither that release (`2026.1`) nor that exact patch gets a note, like the launcher's "outdated!". With **Check the game log after you play** switched on in Settings (it's off by default), Flotilla watches for UBOAT closing and reads the newest `output_log*.txt` a couple of seconds later. It looks for compile errors, exceptions and their stack traces, failed assembly loads and Harmony patches, and the `[Tag] ERROR` lines mods write. Each problem is pinned on a mod by its folder path, the namespaces in its source and DLLs, or the log tag it uses. If an enabled mod logged an error, a pop-up names it and offers to save the full error log to Downloads. Closing Flotilla while UBOAT runs asks first, because the check can't happen then. Errors in a mod that's been updated since are ignored. A problem that repeats is counted ("4,812 times last game", with a warning that errors that often can lower the frame rate), and the most frequent comes first. A failed assembly load names the mod that provides the missing assembly and says whether it's off, loads too late or isn't installed. Compile errors about missing types or members, and missing method, field or type errors, say the mod was likely made for an older UBOAT, and mention the update when one is ready.

**Error reports.** If Flotilla itself crashes, it sends the error log (Flotilla and Windows version plus the exception, with the Windows username removed) to the Worker's `/report` endpoint, which posts it as a `.txt` file to the Discord webhook in the Worker secret `WEBHOOK_URL` (a `https://discord.com/api/webhooks/...` address), waiting and retrying once if Discord rate-limits it. Reports that can't be sent wait in `%LOCALAPPDATA%\Flotilla\unsent reports` for the next start. Switch it off in Settings.

**Play UBOAT** empties UBOAT's `Cache`, `Temp` and `Data Sheets` folders first (textures, compiled mod code and compiled data sheets, which the game rebuilds) when the mods that are on, their order, their files or the game version changed since the last clear, so leftovers from old or removed mods can't come back. When nothing changed the cache is kept, because rebuilding it is most of a big mod pack's load time. The fingerprint of the last clear is `CacheStamp` in `settings.json`. Turn it off in Settings.

**While UBOAT runs**, Keep Flotilla quiet while UBOAT runs (on by default) pauses Flotilla's own background work: the featured list, update checks, looking up what mods need, and its animations. The top bar says Flotilla is resting. Give UBOAT priority while it runs (off by default) sets the game to High priority in Windows and back to Normal when switched off.

**Settings** holds the switches for all of the above, update checks, a different UBOAT data folder, and shortcuts to the data, Mods and Flotilla folders and the game log. Favourites (the star on a mod) are kept there too.

**Themes.** Each theme is one file in `src/Flotilla/Theme` holding every colour, font and corner shape. Only Sonar (Flotilla's teal look) is offered for now. `Logbook.xaml` is still there, hidden: `Themes.Apply` in `src/Flotilla/UI/Themes.cs` says how to bring it back.

**Share codes.** Share on Installed (or Share it with a code on a profile) asks how long the code should last (24, 48 or 72 hours), then uploads the mod list, the same text Export writes, to the Flotilla API and returns a code like `FLT-8K2QR7` and a link, `https://flotilla-api.tyrrh019-851.workers.dev/s/8K2QR7`. When that time is up, the code and link stop working, and the Worker deletes expired codes the next time anyone shares. The link opens a page listing the mods, with a button that opens `flotilla://setup/8K2QR7`; Flotilla registers that scheme for the current user when the published exe runs, and a second copy hands the link to the one already open. A setup over 256 KB of text (a few thousand mods) is too big for a code, and Flotilla says so and suggests Export instead. Use a code shows what would change (mods to install, switch on or off, versions, order) before matching it, then saves the setup as a profile.

**History and rollback.** Every change to which mods are on, their order or their versions is saved to `history.json` (the last 40, quick changes merged), and Restore puts a setup back, downloading other versions where needed. Before updating a mod it installed, Flotilla copies the old version to `backups\<id>` (mods over 1.5 GB keep only the version number), so Roll back to the previous version on the mod's menu can swap it back and pin that version. Uninstalling a mod deletes its backup, and Settings shows how much space the backups take, with a button to delete them.

**Find a broken mod** (beta) splits the switched-on mods in half and asks after each game whether the problem is still there. A mod is never tested without what it needs or the mod it patches (patches are found the same way as in Find best load order), so a half-installed patch can't be blamed. If you answer before UBOAT has run since the test was set up, it asks you to confirm. Undo last answer steps back one test. After each test game it reads the log and notes which of the mods that were on logged errors, as a clue only. It saves its progress, so closing Flotilla mid-way is fine, and Stop restores the original setup.

**Works on my version.** A mod's page asks whether it works on your UBOAT version. Votes are one per install, mod and game version; cards show the share that says it works once three people have voted.

**Mod settings.** Config files in UBOAT's folder and `ModData` are linked to the mod whose source or DLLs mention them. The gear on a mod shows each `key=value` or JSON setting as a switch or a box, with the comment above it as its description, and saving only changes the edited values.

**What's new** on a mod's page, and in the prompt before updating, comes from the mod's Workshop change notes.

**Updating Flotilla itself** happens while it's open: Flotilla asks GitHub for the latest release of this repo when it starts and every minute after, sending the release's ETag so the checks don't use up GitHub's limit. When a newer version is out, it quietly downloads that release's `Flotilla.exe` as `Flotilla.exe.new` and checks its size and SHA-256 against what GitHub reports. Once UBOAT isn't running, it offers to update. Saying yes swaps it in place of the running copy (the old one is kept as `Flotilla.exe.old` until the next start) and opens the new version in the same place and tab before the old window closes, so it doesn't look like a restart. Not now keeps the download, and that version isn't offered again until Flotilla restarts. Drafts and pre-releases are never offered. To ship an update:

1. Raise `<Version>` in `src/Flotilla/Flotilla.csproj`, for example to `0.1.3`.
2. Publish `Flotilla.exe` with the command above.
3. Make a GitHub release whose name contains that version, like `V0.1.3` (a letter for a hotfix such as `v0.1.3b` counts as newer than `0.1.3`), and attach the exe named exactly `Flotilla.exe`. The release notes are shown in the update prompt.

**Help** is the ? button at the top right or How Flotilla works at the bottom of the sidebar (or F1). It opens a short tour, which also shows the first time Flotilla starts. Other shortcuts: Ctrl+F searches (Browse, or the Installed list when that's open), Ctrl+1 and Ctrl+2 switch tabs, F5 refreshes the Workshop list, and Alt with the arrow keys moves a mod in the load order.

**Credits** shows who makes Flotilla, what donations pay for, a few counts of what Flotilla has done on this PC (mods installed, updates applied, games launched through Flotilla and mod errors caught, kept in `settings.json` from 1.2 on), and thanks to Pingu565 for helping Flotilla find its players.

**Uninstalling** deletes the mod's folder for anything in the Mods folder. A Steam subscription can only be removed by Steam, so for those the button opens the mod in Steam, where you unsubscribe.

Changes are refused while `UBOAT.exe` or the UBOAT launcher is running, because the game reads and rewrites `modlist.txt` itself. Play UBOAT in the sidebar starts the game through Steam.

## Limits

- Mods that need the Type IX DLC, or that Steam won't hand out anonymously, fail with a message saying so. Subscribe to those in Steam instead (Open in Steam on the mod's page), and Flotilla picks them up. SteamCMD can't borrow the Steam app's sign-in, so Flotilla never asks for Steam credentials.
- `Flotilla.exe` isn't code-signed, so Windows SmartScreen warns the first time it runs, and PCs with Smart App Control switched on block it.
- Updates are checked against the Workshop list Flotilla last read, which refreshes every time it starts. A mod Flotilla installed before this version, or one SteamCMD has no record of, won't show an Update button until you reinstall it once.
- Load order controls data sheets, textures and sounds. Each code mod sets its own Harmony patch order.

Flotilla only ever downloads from Steam to your own machine and doesn't re-host anything. Every mod page links back to its Workshop page.
