using Flotilla.Mods;

namespace Flotilla.UI;

public sealed class InstalledMod(ModFolder folder) : Observable
{
    public ModFolder Folder { get; } = folder;
    public string Key => Folder.Key;
    public ModSource Source => Folder.Source;
    public ulong? WorkshopId => Folder.WorkshopId;

    public ModTile? Tile
    {
        get;
        set
        {
            if (Set(ref field, value)) Raise(nameof(Name));
        }
    }

    public bool Enabled { get; set => Set(ref field, value); }
    public string Ordinal { get; set => Set(ref field, value); } = "";

    public bool Confirming { get; set => Set(ref field, value); }

    public string Name => Tile?.Title ?? (Folder.Manifest?.Name is { Length: > 0 } name ? name : Key);

    public string Details
    {
        get
        {
            if (Source == ModSource.Missing) return "Its files are gone. Remove it from the list, or reinstall it.";
            if (Folder.Manifest is not { } manifest) return "No manifest";

            var parts = new List<string>();
            if (manifest.Version.Length > 0) parts.Add($"Version {manifest.Version}");
            var releases = manifest.GameVersions.Select(v => v.Split(' ')[0]).Distinct().ToList();
            if (releases.Count > 0) parts.Add($"made for {string.Join(" and ", releases)}");
            return parts.Count > 0 ? string.Join(", ", parts) : "No version given";
        }
    }

    public string SourceLabel => Source switch
    {
        ModSource.Steam => "Steam",
        ModSource.Download => "Flotilla",
        ModSource.Local => "Local",
        _ => "Missing",
    };

    public string SourceTip => Source switch
    {
        ModSource.Steam => "Subscribed in Steam. Steam keeps it up to date.",
        ModSource.Download => "Installed by Flotilla with SteamCMD.",
        ModSource.Local => "A folder you put in UBOAT's Mods folder yourself.",
        _ => "Listed in modlist.txt, but its files aren't on this PC.",
    };

    public string RemoveTip => Source switch
    {
        ModSource.Steam => "Unsubscribe in Steam",
        ModSource.Missing => "Remove from the list",
        _ => "Uninstall",
    };
}
