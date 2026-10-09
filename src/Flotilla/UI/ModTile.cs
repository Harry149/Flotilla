using System.Text.RegularExpressions;
using System.Windows.Media;
using Flotilla.Steam;
using Flotilla.Text;

namespace Flotilla.UI;

public enum TileState { Available, Queued, Installing, Installed, Outdated, Subscribed, Failed }

public sealed record Fact(string Label, string Value);

public sealed class ModTile : Observable
{
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    readonly LazyImage thumbnail;
    readonly LazyImage preview;
    readonly LazyImage blur;
    string? summary;

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

    public string Summary => summary ??= Shorten(Spaces.Replace(BBCode.Parse(Item.Description).Prose, " ").Trim(), 190);

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
