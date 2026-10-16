using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Flotilla.UI;

namespace Flotilla.Views;

public sealed record HeroDot(int Index, bool Current);

public sealed class HeroRow(IReadOnlyList<ModTile> tiles, bool featured) : Observable
{
    public IReadOnlyList<ModTile> Tiles { get; } = tiles;
    public string Label { get; } = featured ? "Featured" : "Most subscribed on the Workshop";
    public ModTile Tile => Tiles[Index];
    public IReadOnlyList<HeroDot> Dots => Tiles.Count < 2 ? [] : [.. Tiles.Select((_, i) => new HeroDot(i, i == Index))];
    public bool Paused { get; set; }

    int Index
    {
        get;
        set
        {
            if (!Set(ref field, value)) return;
            Raise(nameof(Tile));
            Raise(nameof(Dots));
        }
    }

    public void Next() => Index = (Index + 1) % Tiles.Count;

    public void Show(int index) => Index = index;
}

public sealed record SectionRow(string Title, string Count, string Sort, bool HideInstalled, bool FavouritesOnly, bool Clearable = false)
{
    public bool Popular => Sort == "popular";
    public bool Updated => Sort == "updated";
    public bool Newest => Sort == "newest";
    public bool ByName => Sort == "name";
}

public sealed record TileRow(IReadOnlyList<ModTile> Tiles, int Columns);

public sealed record MessageRow(string Title, string Text, bool CanRetry = false, bool Scanning = false);

public partial class WorkshopView : UserControl
{
    const double CardWidth = 220;
    const double Gap = 18;
    const double Gutters = 56;

    string query = "";
    string sort = "popular";
    bool hideInstalled;
    bool favouritesOnly;
    ulong author;
    string authorName = "";
    int columns = 4;
    HeroRow? hero;
    readonly DispatcherTimer rotate = new() { Interval = TimeSpan.FromSeconds(8) };

    public WorkshopView()
    {
        InitializeComponent();
        rotate.Tick += (_, _) =>
        {
            if (hero is { Paused: false } && !Library.Resting) hero.Next();
        };
        rotate.Start();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is Library old) old.PropertyChanged -= OnLibraryChanged;
            if (e.NewValue is Library library) library.PropertyChanged += OnLibraryChanged;
            Refresh();
        };
    }

    public event Action<ModTile>? Opened;
    public event Action<ModTile>? InstallRequested;
    public event Action? RetryRequested;

    public void Filter(string text)
    {
        text = text.Trim();
        if (text == query) return;

        query = text;
        Refresh();
        Visuals.Find<ScrollViewer>(Rows)?.ScrollToTop();
    }

    public bool ShowsAuthor => author != 0;

    public void ShowAuthor(ulong id, string name)
    {
        author = id;
        authorName = name;
        Refresh();
        Visuals.Find<ScrollViewer>(Rows)?.ScrollToTop();
    }

    public void ClearAuthor()
    {
        if (author == 0) return;

        author = 0;
        Refresh();
        Visuals.Find<ScrollViewer>(Rows)?.ScrollToTop();
    }

    void OnClearAuthor(object sender, RoutedEventArgs e) => ClearAuthor();

    void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Library.Tiles) or nameof(Library.WorkshopError) or nameof(Library.FeaturedIds)
            || e.PropertyName == nameof(Library.FavouriteCount) && favouritesOnly) Refresh();
    }

    void OnSort(object sender, RoutedEventArgs e)
    {
        var next = (string)((FrameworkElement)sender).Tag;
        if (next == sort) return;

        sort = next;
        Refresh();
    }

    void OnHideInstalled(object sender, RoutedEventArgs e)
    {
        hideInstalled = !hideInstalled;
        Refresh();
    }

    void OnFavouritesOnly(object sender, RoutedEventArgs e)
    {
        favouritesOnly = !favouritesOnly;
        Refresh();
    }

    void OnResized(object sender, SizeChangedEventArgs e)
    {
        var fit = Math.Max(1, (int)((e.NewSize.Width - Gutters + Gap) / (CardWidth + Gap)));
        if (fit == columns) return;

        columns = fit;
        Refresh();
    }

    void OnCard(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ToggleButton) return;
        Raise(Opened, sender);
    }

    void OnHeroOpen(object sender, RoutedEventArgs e) => Raise(Opened, sender);

    void OnHeroInstall(object sender, RoutedEventArgs e) => Raise(InstallRequested, sender);

    void OnRetry(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    void OnHeroEnter(object sender, MouseEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is HeroRow row) row.Paused = true;
    }

    void OnHeroLeave(object sender, MouseEventArgs e)
    {
        if (hero is not null) hero.Paused = false;
    }

    void OnHeroDot(object sender, RoutedEventArgs e)
    {
        if (hero is not null && ((FrameworkElement)sender).DataContext is HeroDot dot) hero.Show(dot.Index);
    }

    void OnHeroSized(object sender, SizeChangedEventArgs e)
    {
        var radius = ((CornerRadius)FindResource("RoundLarge")).TopLeft;
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }

    static void Raise(Action<ModTile>? handler, object sender)
    {
        if (((FrameworkElement)sender).DataContext is ModTile tile) handler?.Invoke(tile);
    }

    void Refresh()
    {
        if (Rows is null || DataContext is not Library library) return;

        var all = library.Tiles;
        if (all.Count == 0)
        {
            Rows.ItemsSource = new object[]
            {
                library.WorkshopError is null
                    ? new MessageRow("Reading the Steam Workshop", "The first visit takes about half a minute. After that the list opens straight away.", Scanning: true)
                    : new MessageRow("Couldn't reach the Steam Workshop", "Check your internet connection, then try again.", CanRetry: true),
            };
            return;
        }

        var found = all.Where(t => t.Matches(query)
            && (author == 0 || t.Item.Creator == author)
            && (!favouritesOnly || t.IsFavourite)
            && !(hideInstalled && t.State is TileState.Installed or TileState.Outdated or TileState.Subscribed));
        var tiles = (sort switch
        {
            "updated" => found.OrderByDescending(t => t.Item.Updated),
            "newest" => found.OrderByDescending(t => t.Item.Created),
            "name" => found.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => found.OrderByDescending(t => t.Item.Subscribers),
        }).ToList();

        var rows = new List<object>();
        if (author != 0)
        {
            rows.Add(new SectionRow($"Mods by {authorName}", tiles.Count == 1 ? "1 mod" : $"{tiles.Count:N0} mods", sort, hideInstalled, favouritesOnly, Clearable: true));
        }
        else if (query.Length == 0)
        {
            rows.Add(Hero(library));
            rows.Add(new SectionRow("All mods", $"{tiles.Count:N0} mods", sort, hideInstalled, favouritesOnly));
        }
        else
        {
            rows.Add(new SectionRow("Results", $"{tiles.Count:N0} of {all.Count:N0}", sort, hideInstalled, favouritesOnly));
        }

        rows.AddRange(tiles.Chunk(columns).Select(chunk => new TileRow(chunk, columns)));
        if (tiles.Count == 0 && query.Length > 0)
            rows.Add(new MessageRow($"Nothing matches “{query}”", hideInstalled || favouritesOnly ? "A filter is on, so some mods aren't shown." : "Try part of the name, or a tag such as Crew or Graphics."));
        else if (tiles.Count == 0 && favouritesOnly)
            rows.Add(new MessageRow("No favourites here yet", "Press the star on a mod to save it for later."));
        else if (tiles.Count == 0)
            rows.Add(new MessageRow(author != 0 ? $"You have every mod by {authorName}" : "You have every mod", "Turn off Hide installed to see them all again."));

        Rows.ItemsSource = rows;
    }

    HeroRow Hero(Library library)
    {
        var byId = library.Tiles.ToDictionary(t => t.Id);
        var featured = library.FeaturedIds.Select(byId.GetValueOrDefault).OfType<ModTile>().ToList();
        List<ModTile> picks = featured.Count > 0 ? featured : [library.Tiles.MaxBy(t => t.Item.Subscribers)!];

        if (hero is null || !hero.Tiles.SequenceEqual(picks)) hero = new HeroRow(picks, featured.Count > 0);
        return hero;
    }
}
