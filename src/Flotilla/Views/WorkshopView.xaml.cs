using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Flotilla.UI;

namespace Flotilla.Views;

public sealed record HeroRow(ModTile Tile);

public sealed record SectionRow(string Title, string Count, string Sort)
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
    int columns = 4;

    public WorkshopView()
    {
        InitializeComponent();
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

    void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Library.Tiles) or nameof(Library.WorkshopError)) Refresh();
    }

    void OnSort(object sender, RoutedEventArgs e)
    {
        var next = (string)((FrameworkElement)sender).Tag;
        if (next == sort) return;

        sort = next;
        Refresh();
    }

    void OnResized(object sender, SizeChangedEventArgs e)
    {
        var fit = Math.Max(1, (int)((e.NewSize.Width - Gutters + Gap) / (CardWidth + Gap)));
        if (fit == columns) return;

        columns = fit;
        Refresh();
    }

    void OnCard(object sender, RoutedEventArgs e) => Raise(Opened, sender);

    void OnHeroOpen(object sender, RoutedEventArgs e) => Raise(Opened, sender);

    void OnHeroInstall(object sender, RoutedEventArgs e) => Raise(InstallRequested, sender);

    void OnRetry(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    void OnHeroSized(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);

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

        var found = all.Where(t => t.Matches(query));
        var tiles = (sort switch
        {
            "updated" => found.OrderByDescending(t => t.Item.Updated),
            "newest" => found.OrderByDescending(t => t.Item.Created),
            "name" => found.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => found.OrderByDescending(t => t.Item.Subscribers),
        }).ToList();

        var rows = new List<object>();
        if (query.Length == 0)
        {
            rows.Add(new HeroRow(all.MaxBy(t => t.Item.Subscribers)!));
            rows.Add(new SectionRow("All mods", $"{tiles.Count:N0} mods", sort));
        }
        else
        {
            rows.Add(new SectionRow("Results", $"{tiles.Count:N0} of {all.Count:N0}", sort));
        }

        rows.AddRange(tiles.Chunk(columns).Select(chunk => new TileRow(chunk, columns)));
        if (tiles.Count == 0) rows.Add(new MessageRow($"Nothing matches “{query}”", "Try part of the name, or a tag such as Crew or Graphics."));

        Rows.ItemsSource = rows;
    }
}
