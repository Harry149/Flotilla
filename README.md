# Flotilla

A mod manager for UBOAT. Browse the Steam Workshop, install and update mods without Steam, switch them on and off, and set their load order.

## Build and run

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), or Visual Studio 2026 with the .NET desktop workload.

```
dotnet run --project src/Flotilla
```

To make a single `Flotilla.exe` you can hand to someone:

```
dotnet publish src/Flotilla -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
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

**Export and Import** share a setup. Export saves a text file listing every mod that's switched on, in load order, one per line: the Workshop ID, the exact version (the Steam manifest ID, or `latest` when Flotilla doesn't know it), and the name. Import reads that file and installs each mod at that version with SteamCMD's `download_depot`. If Steam no longer has that version, Import installs the latest one and says so. It then switches on exactly the mods in the file, in its order. Mods that aren't in the file stay installed, switched off. A mod you subscribe to in Steam is used as it is, because Steam decides its version. A local mod is only switched on if a folder with that name is already in the Mods folder. Flotilla records the versions it installed this way in `steamcmd\versions.txt`, so the Update button still knows when the Workshop has something newer.

**Uninstalling** deletes the mod's folder for anything in the Mods folder. A Steam subscription can only be removed by Steam, so for those the button opens the mod in Steam, where you unsubscribe.

Changes are refused while `Flotilla.exe` is running, because the game reads and rewrites `modlist.txt` itself. Play UBOAT in the sidebar starts the game through Steam.

## Limits

- Mods that need the Type IX DLC, or that Steam won't hand out anonymously, fail with a message saying so. Subscribe to those in Steam instead.
- Dependencies listed on a Workshop page aren't installed automatically.
- Updates are checked against the Workshop list Flotilla last read, which refreshes every time it starts. A mod Flotilla installed before this version, or one SteamCMD has no record of, won't show an Update button until you reinstall it once.
- Load order controls data sheets, textures and sounds. Each code mod sets its own Harmony patch order.

Flotilla only ever downloads from Steam to your own machine and doesn't re-host anything. Every mod page links back to its Workshop page.
