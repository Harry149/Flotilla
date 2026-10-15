using System.Text.RegularExpressions;
using System.Windows.Media;
using Flotilla.Mods;
using Flotilla.Steam;
using Flotilla.Text;

namespace Flotilla.UI;

public enum TileState { Available, Queued, Installing, Installed, Outdated, Subscribed, Failed }

public sealed record Fact(string Label, string Value);

public sealed class ModTile : Observable
{
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    static readonly SteamProfiles Profiles = new(Web.Client);

    readonly LazyImage thumbnail;
    readonly LazyImage preview;
    readonly LazyImage blur;
    string? summary;
    string? author;
    bool lookingUp;
    bool favourite;

    public ModTile(WorkshopItem item)
    {
        Item = item;
        thumbnail = new LazyImage(Images.Sized(item.PreviewUrl, 512), 480, () => Raise(nameof(Thumbnail)));
        preview = new LazyImage(item.PreviewUrl, 900, () => Raise(nameof(Preview)));
        blur = new LazyImage(Images.Sized(item.PreviewUrl, 512), 40, () => Raise(nameof(Blur)));
    }

    public WorkshopItem Item { get; private set; }
    public ulong Id => Item.Id;
    public string Title => Item.Title;
    public string Subscribers => Item.Subscribers.ToString("N0");
    public string SubscribersLong => Item.Subscribers == 1 ? "1 subscriber" : $"{Item.Subscribers:N0} subscribers";
    public string Updated => $"Updated {Ago(Item.Updated)}";
    public string Size => FormatSize(Item.Size);
    public string? Tag => Item.Tags.FirstOrDefault();
    public IReadOnlyList<string> Tags => Item.Tags;
    public ImageSource? Thumbnail => thumbnail.Value;
    public ImageSource? Preview => preview.Value;
    public ImageSource? Blur => blur.Value;

    public string CardTitle => GameLog.SplitTitle(Title).Title;
    public string? MadeFor => GameLog.SplitTitle(Title).Version;
    public bool MadeForOther => MadeFor is { } made && game is { } have && !GameLog.Supports([made], have);
    public string? MadeForTip => MadeFor is not { } made ? null
        : game is null ? $"Made for UBOAT {made}."
        : MadeForOther ? $"Made for UBOAT {made}. You have {game}, so it may not work."
        : $"Made for UBOAT {made}, the version you have.";

    string? game;

    public void ShowGame(string? version)
    {
        if (game == version) return;
        game = version;
        Raise(nameof(MadeForOther));
        Raise(nameof(MadeForTip));
    }

    public string Summary => summary ??= Shorten(Spaces.Replace(BBCode.Parse(Item.Description).Prose, " ").Trim(), 190);

    public static event Action<ModTile>? FavouriteChanged;

    public bool IsFavourite
    {
        get => favourite;
        set
        {
            if (!Set(ref favourite, value)) return;
            FavouriteChanged?.Invoke(this);
        }
    }

    public void LoadFavourite(bool value) => Set(ref favourite, value, nameof(IsFavourite));

    public VoteCount? Votes { get; private set; }
    public bool? MyVote { get; private set; }
    public bool HasVotes => Votes is { Total: >= 3 };
    public string VoteBadge => Votes is { } v ? $"{v.Percent}%" : "";
    public bool VotesMostlyBroken => Votes is { Total: >= 3, Percent: < 60 };
    public string VoteSummary => Votes switch
    {
        null or { Total: 0 } => "No one has said yet. Be the first.",
        { Total: 1 } v => v.Works == 1 ? "1 sailor says it works." : "1 sailor says it's broken.",
        var v => $"{v.Works} of {v.Total} sailors say it works ({v.Percent}%).",
    };
    public bool VotedWorks => MyVote == true;
    public bool VotedBroken => MyVote == false;

    public void ShowVotes(VoteCount? votes, bool? mine)
    {
        Votes = votes;
        MyVote = mine;
        foreach (var name in new[] { nameof(Votes), nameof(MyVote), nameof(HasVotes), nameof(VoteBadge), nameof(VotesMostlyBroken), nameof(VoteSummary), nameof(VotedWorks), nameof(VotedBroken) })
            Raise(name);
    }

    public bool HasAuthor => Item.Creator != 0;

    public string Author
    {
        get
        {
            if (author is null && !lookingUp && HasAuthor) _ = LookUpAuthorAsync();
            return author ?? "this author";
        }
    }

    async Task LookUpAuthorAsync()
    {
        lookingUp = true;
        author = await Profiles.NameAsync(Item.Creator);
        if (author is not null) Raise(nameof(Author));
    }

    public TileState State { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }

    public bool CanInstall => State is TileState.Available or TileState.Installed or TileState.Outdated or TileState.Failed;
    public bool IsInstalling => State is TileState.Queued or TileState.Installing;
    public bool ShowsUpdate => State is TileState.Outdated || IsInstalling;
    public string? UpdateNote => State == TileState.Outdated ? $"A newer version came out {Ago(Item.Updated)}." : null;

    public string Action => State switch
    {
        TileState.Queued => "Waiting to install",
        TileState.Installing => Progress > 0 ? $"Installing {Progress * 100:0}%" : "Installing",
        TileState.Installed => "Reinstall",
        TileState.Outdated => "Update",
        TileState.Subscribed => "Installed through Steam",
        TileState.Failed => "Try again",
        _ => "Install",
    };

    public string? Badge => State switch
    {
        TileState.Installed or TileState.Subscribed => "Installed",
        TileState.Outdated => "Update",
        TileState.Queued => "Queued",
        TileState.Installing => Progress > 0 ? $"{Progress * 100:0}%" : "Installing",
        TileState.Failed => "Failed",
        _ => null,
    };

    public IReadOnlyList<Fact> Facts =>
    [
        new("Size", Size),
        new("Updated", Item.Updated.ToLocalTime().ToString("d MMM yyyy")),
        new("Published", Item.Created.ToLocalTime().ToString("d MMM yyyy")),
        new("Subscribers", Subscribers),
        new("Workshop ID", Id.ToString()),
    ];

    public bool Matches(string query) =>
        query.Length == 0
        || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Item.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    public void Refresh(WorkshopItem item)
    {
        Item = item;
        summary = null;
        if (author is null) lookingUp = false;
        Raise(string.Empty);
    }

    public void Queue() => Become(TileState.Queued);

    public void Downloading(double progress)
    {
        Progress = progress;
        Become(TileState.Installing);
    }

    public void Finish() => Become(TileState.Installed);

    public void Fail(string reason)
    {
        Error = $"Couldn't install: {reason.TrimEnd('.')}.";
        Become(TileState.Failed);
    }

    public void Settle(bool subscribed, bool installed, bool outdated)
    {
        if (IsInstalling) return;

        if (subscribed) Become(TileState.Subscribed);
        else if (installed) Become(outdated ? TileState.Outdated : TileState.Installed);
        else if (State != TileState.Failed) Become(TileState.Available);
    }

    void Become(TileState state)
    {
        if (state == State && state is not (TileState.Installing or TileState.Failed)) return;

        if (state != TileState.Installing) Progress = 0;
        if (state != TileState.Failed) Error = null;
        State = state;
        Raise(string.Empty);
    }

    static string Shorten(string text, int length)
    {
        if (text.Length <= length) return text;
        var cut = text.LastIndexOf(' ', length);
        return text[..(cut > length / 2 ? cut : length)].TrimEnd(',', '.', ';', ':', '-') + "…";
    }

    static string Ago(DateTime utc)
    {
        var days = (int)(DateTime.UtcNow - utc).TotalDays;
        return days switch
        {
            < 1 => "today",
            1 => "yesterday",
            < 7 => $"{days} days ago",
            < 14 => "last week",
            < 31 => $"{days / 7} weeks ago",
            < 62 => "last month",
            < 365 => $"{days / 30} months ago",
            _ => utc.ToLocalTime().ToString("MMM yyyy"),
        };
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };
}
