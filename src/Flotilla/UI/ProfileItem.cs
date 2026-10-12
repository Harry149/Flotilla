using Flotilla.Mods;

namespace Flotilla.UI;

public sealed record RequirementRow(string Name, string State, bool Ready, ModTile? Tile);

public sealed record SnapshotItem(Snapshot Snapshot, SetupDifference Difference)
{
    public string When
    {
        get
        {
            var local = Snapshot.Taken.ToLocalTime();
            var minutes = (DateTime.Now - local).TotalMinutes;
            return minutes switch
            {
                < 1 => "Just now",
                < 60 => $"{(int)minutes} min ago",
                _ when local.Date == DateTime.Today => $"Today, {local:HH:mm}",
                _ when local.Date == DateTime.Today.AddDays(-1) => $"Yesterday, {local:HH:mm}",
                _ => local.ToString("d MMM, HH:mm"),
            };
        }
    }

    public string Reason => Snapshot.Reason;
    public string Count => Snapshot.Mods.Count == 1 ? "1 mod on" : $"{Snapshot.Mods.Count} mods on";
    public string Change => Difference.Same ? "This is your setup now" : $"Compared with now: {Difference.Describe()}";
    public bool IsCurrent => Difference.Same;
}

public sealed record GameAlert(IReadOnlyList<(string Mod, string Error)> Mods, int Warnings, string Report);

public sealed class ProfileItem(Profile profile) : Observable
{
    public Profile Profile
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(Name));
            Raise(nameof(Count));
        }
    } = profile;

    public string Name => Profile.Name;
    public string Count => Profile.Mods.Count.ToString();
    public bool IsActive { get; set => Set(ref field, value); }
}
