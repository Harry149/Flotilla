using Flotilla.Mods;

namespace Flotilla.UI;

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
