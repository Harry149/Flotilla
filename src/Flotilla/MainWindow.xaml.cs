using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Flotilla.UI;

namespace Flotilla;

public partial class MainWindow : Window
{
    readonly Library library = new();
    ModTile? open;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = library;

        Browse.Opened += Open;
        Browse.InstallRequested += library.Install;
        Browse.RetryRequested += () => _ = library.RefreshWorkshopAsync();
        Details.Closed += CloseMod;
        Details.InstallRequested += library.Install;
        InstalledPage.BrowseRequested += () => BrowseTab.IsChecked = true;
        library.PropertyChanged += OnLibraryChanged;

        Loaded += async (_, _) => await library.StartAsync();
        Activated += (_, _) => library.Rescan();
        StateChanged += (_, _) => FitToScreen();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && open is not null && BrowseTab.IsChecked == true)
        {
            CloseMod();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Library.Tiles))
            SearchBox.Tag = library.Tiles.Count > 0 ? $"Search {library.Tiles.Count:N0} mods" : "Search mods";
    }

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (open is not null || BrowseTab.IsChecked != true)
        {
            open = null;
            BrowseTab.IsChecked = true;
            ShowView();
        }
        Browse.Filter(SearchBox.Text);
    }

    void Open(ModTile tile)
    {
        open = tile;
        Details.DataContext = tile;
        ShowView();
    }

    void CloseMod()
    {
        open = null;
        ShowView();
    }

    void OnTabChanged(object sender, RoutedEventArgs e) => ShowView();

    void ShowView()
    {
        if (InstalledPage is null) return;

        var installed = InstalledTab.IsChecked == true;
        Reveal(InstalledPage, installed);
        Reveal(Details, !installed && open is not null);
        Reveal(Browse, !installed && open is null);
    }

    static void Reveal(UIElement view, bool visible)
    {
        var wasVisible = view.Visibility == Visibility.Visible;
        view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (visible && !wasVisible && SystemParameters.ClientAreaAnimation)
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    void OnPlay(object sender, RoutedEventArgs e) => library.PlayGame();

    void OnDismissToast(object sender, RoutedEventArgs e) => library.Status.Dismiss();

    void FitToScreen()
    {
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = maximized ? new Thickness(frame.Left + 4, frame.Top + 4, frame.Right + 4, frame.Bottom + 4) : new Thickness(0);
        MaximizeGlyph.Data = (Geometry)FindResource(maximized ? "IconRestore" : "IconMaximize");
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
