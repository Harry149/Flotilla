using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Flotilla.UI;

namespace Flotilla;

public partial class MainWindow : Window
{
    sealed record TourStep(string Icon, string Title, string Body, string[] Tips, bool OnInstalled = false);

    static readonly TourStep[] Tour =
    [
        new("IconSonar", "Welcome to Flotilla",
            "Flotilla finds, installs and organises your UBOAT mods. This tour takes about a minute, and the ? button brings it back whenever you like.",
            [
                "Everything you change goes straight into UBOAT's own mod list, so the game and its launcher see it too.",
                "Close UBOAT and its launcher before changing mods. Flotilla reminds you if you forget.",
            ]),
        new("IconBrowse", "Find mods in Browse",
            "Browse shows every mod on the UBOAT Workshop. The banner at the top shows mods we recommend.",
            [
                "Search by name or tag with Ctrl+F. Click a tag or the author on a mod's page to see more like it.",
                "Star mods to save them for later, then use the Favourites filter to find them again.",
                "Click a mod to read about it, then press Install. Mods it needs are installed with it, and you don't need to sign in to Steam.",
            ]),
        new("IconInstalled", "Manage them in Installed",
            "Installed lists every mod on this PC, in the order UBOAT loads them.",
            [
                "Flip the switch to turn a mod on or off. Mods you turn off stay installed.",
                "Drag the dots on the left to change the load order. When two mods change the same thing, the lower one wins.",
                "Double-click a mod to open its page. Right-click it to open its folder, see it on the Workshop, or uninstall it.",
                "A yellow line under a mod means it needs another mod, was made for a different UBOAT version, or broke in your last game. Fix sorts out what it needs.",
            ], OnInstalled: true),
        new("IconLayers", "Save setups as profiles",
            "A profile remembers which mods are on and their order, so you can keep one setup for a full overhaul and another for a lighter game.",
            [
                "Turn on the mods you want, then press + next to Profiles in the sidebar.",
                "Click a profile to switch to it. If a mod in it isn't installed, Flotilla installs it for you.",
                "The tick shows which profile is on. Right-click a profile to update, rename, reorder or delete it.",
            ], OnInstalled: true),
        new("IconUpdate", "Updates and sharing",
            "Flotilla checks the Workshop every time it starts.",
            [
                "A dot next to Installed means updates are ready. Update all gets them in one go.",
                "Export saves your mods, their versions and their order to a file. A friend can Import it to get the exact same setup.",
                "Mods you subscribe to in Steam show up here too, and Steam keeps those up to date.",
            ], OnInstalled: true),
        new("IconPlay", "Ready to dive",
            "Press Play UBOAT in the sidebar to start the game through Steam with your mods.",
            [
                "Play clears UBOAT's cache first, so leftovers from old mods can't cause trouble. You can change that, and more, in Settings.",
                "A few mods need an account that owns UBOAT or a DLC, so they won't install anonymously. Use Open in Steam on their page and subscribe there, and Flotilla picks them up.",
                "F5 refreshes the Workshop list. Ctrl+1 and Ctrl+2 switch between Browse and Installed.",
            ]),
    ];

    readonly Library library = new();
    Settings settings => library.Settings;
    readonly Updater updater = new(Web.Client);
    Update? update;
    ModTile? open;
    int tourStep = -1;
    bool clearingSearch;
    TaskCompletionSource<string?>? dialog;
    Func<string, string?>? validate;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = library;
        RestorePlacement();

        Browse.Opened += Open;
        Browse.InstallRequested += library.Install;
        Browse.RetryRequested += () => _ = library.RefreshWorkshopAsync();
        Details.Closed += CloseMod;
        Details.InstallRequested += library.Install;
        Details.TagRequested += SearchTag;
        Details.AuthorRequested += ShowAuthor;
        Details.OpenRequested += Open;
        InstalledPage.BrowseRequested += () => BrowseTab.IsChecked = true;
        InstalledPage.OpenRequested += Open;
        SettingsPage.TourRequested += () => ShowTour(0);
        SettingsPage.UpdateCheckRequested += () => _ = CheckForUpdateAsync(manual: true);
        SettingsPage.RestartRequested += OfferRestart;
        library.PropertyChanged += OnLibraryChanged;

        Loaded += async (_, _) =>
        {
            if (!settings.TutorialSeen) ShowTour(0);
            _ = CheckForUpdateAsync();
            await library.StartAsync();
        };
        Activated += (_, _) => library.Rescan();
        SourceInitialized += (_, _) => FitToScreen();
        StateChanged += (_, _) => FitToScreen();
        Closing += (_, _) => SavePlacement();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = DialogLayer.IsVisible ? DialogKey(e.Key)
            : TutorialLayer.IsVisible ? TourKey(e.Key)
            : ShortcutKey(e.Key == Key.System ? e.SystemKey : e.Key);
        base.OnPreviewKeyDown(e);
    }

    bool ShortcutKey(Key key)
    {
        var control = Keyboard.Modifiers == ModifierKeys.Control;
        switch (key)
        {
            case Key.F when control:
                SearchBox.Focus();
                SearchBox.SelectAll();
                return true;
            case Key.D1 or Key.NumPad1 when control:
                BrowseTab.IsChecked = true;
                CloseMod();
                return true;
            case Key.D2 or Key.NumPad2 when control:
                InstalledTab.IsChecked = true;
                CloseMod();
                return true;
            case Key.D3 or Key.NumPad3 when control:
                SettingsTab.IsChecked = true;
                CloseMod();
                return true;
            case Key.F1:
                ShowTour(0);
                return true;
            case Key.F5:
                library.Status.Say("Refreshing the Workshop list.");
                _ = library.RefreshWorkshopAsync();
                return true;
            case Key.Escape when SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0:
                SearchBox.Clear();
                return true;
            case Key.Escape when open is not null:
                CloseMod();
                return true;
            case Key.Escape when Browse.IsVisible && Browse.ShowsAuthor:
                Browse.ClearAuthor();
                return true;
            default:
                return false;
        }
    }

    void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Library.Tiles)) UpdateSearchHint();
    }

    void UpdateSearchHint() =>
        SearchBox.Tag = InstalledTab.IsChecked == true ? "Search installed mods"
            : library.Tiles.Count > 0 ? $"Search {library.Tiles.Count:N0} mods"
            : "Search mods";

    void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (clearingSearch) return;
        if (open is not null) CloseMod();

        if (InstalledTab.IsChecked == true) InstalledPage.Filter(SearchBox.Text);
        else Browse.Filter(SearchBox.Text);
    }

    void SearchTag(string tag)
    {
        BrowseTab.IsChecked = true;
        CloseMod();
        SearchBox.Text = tag;
    }

    void ShowAuthor(ModTile tile)
    {
        BrowseTab.IsChecked = true;
        CloseMod();
        clearingSearch = true;
        SearchBox.Clear();
        clearingSearch = false;
        Browse.Filter("");
        Browse.ShowAuthor(tile.Item.Creator, tile.Author);
    }

    void Open(ModTile tile)
    {
        open = tile;
        Details.DataContext = tile;
        Details.BackLabel = InstalledTab.IsChecked == true ? "Installed" : "Browse";
        Details.ShowRequirements([]);
        ShowView();
        _ = ShowRequirementsAsync(tile);
    }

    async Task ShowRequirementsAsync(ModTile tile)
    {
        var rows = await library.RequirementRowsAsync(tile.Id);
        if (open == tile) Details.ShowRequirements(rows);
    }

    void CloseMod()
    {
        open = null;
        ShowView();
    }

    void OnTabClick(object sender, RoutedEventArgs e) => CloseMod();

    void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (InstalledPage is null) return;

        clearingSearch = true;
        SearchBox.Clear();
        clearingSearch = false;
        Browse.Filter("");
        Browse.ClearAuthor();
        InstalledPage.Filter("");
        UpdateSearchHint();
        ShowView();
    }

    void ShowView()
    {
        if (InstalledPage is null) return;

        var installed = InstalledTab.IsChecked == true;
        var settings = SettingsTab.IsChecked == true;
        Reveal(Details, open is not null);
        Reveal(InstalledPage, installed && open is null);
        Reveal(SettingsPage, settings && open is null);
        Reveal(Browse, !installed && !settings && open is null);
        SearchBox.Visibility = settings && open is null ? Visibility.Hidden : Visibility.Visible;
    }

    async void OfferRestart()
    {
        var answer = await Ask("Restart Flotilla?", "Flotilla uses the new data folder after it restarts.", "Restart now", cancel: "Later");
        if (answer is null || Environment.ProcessPath is not { } path) return;

        SavePlacement();
        Shell.Open(path);
        Application.Current.Shutdown();
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

    void OnHelp(object sender, RoutedEventArgs e) => ShowTour(0);

    void ShowTour(int step)
    {
        tourStep = Math.Clamp(step, 0, Tour.Length - 1);
        var page = Tour[tourStep];

        TutorialIcon.Data = (Geometry)FindResource(page.Icon);
        TutorialStep.Text = $"Step {tourStep + 1} of {Tour.Length}";
        TutorialTitle.Text = page.Title;
        TutorialBody.Text = page.Body;
        TutorialTips.ItemsSource = page.Tips;
        TutorialDots.ItemsSource = Enumerable.Range(0, Tour.Length).Select(i => i == tourStep).ToList();
        TutorialBack.Visibility = tourStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        TutorialNext.Content = tourStep == Tour.Length - 1 ? "Let's go" : "Next";

        (page.OnInstalled ? InstalledTab : BrowseTab).IsChecked = true;
        CloseMod();

        if (!TutorialLayer.IsVisible) Reveal(TutorialLayer, true);
        TutorialNext.Focus();
    }

    void CloseTour()
    {
        TutorialLayer.Visibility = Visibility.Collapsed;
        tourStep = -1;
        Dispatcher.BeginInvoke(OfferUpdate);
        if (settings.TutorialSeen) return;

        settings.TutorialSeen = true;
        settings.Save();
        BrowseTab.IsChecked = true;
    }

    bool TourKey(Key key)
    {
        switch (key)
        {
            case Key.Escape: CloseTour(); return true;
            case Key.Right or Key.Enter: OnTutorialNext(this, new RoutedEventArgs()); return true;
            case Key.Left when tourStep > 0: ShowTour(tourStep - 1); return true;
            default: return key is not (Key.Tab or Key.Space);
        }
    }

    void OnTutorialNext(object sender, RoutedEventArgs e)
    {
        if (tourStep >= Tour.Length - 1) CloseTour();
        else ShowTour(tourStep + 1);
    }

    void OnTutorialBack(object sender, RoutedEventArgs e) => ShowTour(tourStep - 1);

    void OnTutorialClose(object sender, RoutedEventArgs e) => CloseTour();

    Task<string?> Ask(string title, string text, string confirm, string? input = null, bool danger = false, Func<string, string?>? check = null, string cancel = "Cancel")
    {
        dialog?.TrySetResult(null);
        dialog = new TaskCompletionSource<string?>();
        validate = check;

        DialogTitle.Text = title;
        DialogText.Text = text;
        DialogHint.Text = "";
        DialogConfirm.Content = confirm;
        DialogCancel.Content = cancel;
        DialogConfirm.Style = (Style)FindResource(danger ? "DangerButton" : "Primary");
        DialogInput.Visibility = input is null ? Visibility.Collapsed : Visibility.Visible;
        DialogInput.Text = input ?? "";
        CheckDialogInput();

        Reveal(DialogLayer, true);
        Dispatcher.BeginInvoke(() =>
        {
            if (input is null) DialogConfirm.Focus();
            else
            {
                DialogInput.Focus();
                DialogInput.SelectAll();
            }
        });
        return dialog.Task;
    }

    void CloseDialog(string? result)
    {
        DialogLayer.Visibility = Visibility.Collapsed;
        var pending = dialog;
        dialog = null;
        pending?.TrySetResult(result);
        Dispatcher.BeginInvoke(OfferUpdate);
    }

    async Task CheckForUpdateAsync(bool manual = false)
    {
        if (!manual)
        {
            Updater.CleanUp();
            if (!library.Settings.CheckForUpdates) return;
        }

        try
        {
            update = await updater.CheckAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (manual) library.Status.Warn($"Couldn't check for updates: {e.Message}");
            return;
        }

        if (manual && update is null) library.Status.Say($"You have the latest Flotilla, {Updater.Current}.");
        OfferUpdate();
    }

    async void OfferUpdate()
    {
        if (update is not { } offer || TutorialLayer.IsVisible || DialogLayer.IsVisible) return;
        update = null;

        var notes = offer.Notes.Length > 420 ? offer.Notes[..420].TrimEnd() + "…" : offer.Notes;
        var text = $"You have {Updater.Current}. {(Updater.CanReplaceItself ? "Flotilla downloads it, then restarts." : "This copy can't update itself, so the download page opens.")}";
        if (notes.Length > 0) text += $"\n\nWhat's new:\n{notes}";

        var answer = await Ask($"Flotilla {offer.Version} is out", text, "Update now", cancel: "Not now");
        if (answer is null) return;

        if (!Updater.CanReplaceItself)
        {
            Shell.Open(offer.Page);
            return;
        }

        try
        {
            library.Status.Work("Updating Flotilla", $"Downloading {offer.Version}");
            var downloaded = await updater.DownloadAsync(offer, new Progress<double>(p =>
                library.Status.Work("Updating Flotilla", $"Downloading {offer.Version}, {p * 100:0}%", p)));
            SavePlacement();
            Updater.Install(downloaded);
            Application.Current.Shutdown();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            library.Status.Idle();
            library.Status.Warn($"Couldn't update Flotilla: {e.Message} You can download it from the releases page instead.");
            Shell.Open(offer.Page);
        }
    }

    bool DialogKey(Key key)
    {
        switch (key)
        {
            case Key.Escape: CloseDialog(null); return true;
            case Key.Enter when DialogConfirm.IsEnabled: CloseDialog(DialogInput.Text.Trim()); return true;
            default: return false;
        }
    }

    void OnDialogInput(object sender, TextChangedEventArgs e) => CheckDialogInput();

    void CheckDialogInput()
    {
        if (DialogInput.Visibility != Visibility.Visible)
        {
            DialogConfirm.IsEnabled = true;
            return;
        }

        var text = DialogInput.Text.Trim();
        var problem = text.Length == 0 ? "" : validate?.Invoke(text) ?? "";
        DialogHint.Text = problem;
        DialogConfirm.IsEnabled = text.Length > 0 && problem.Length == 0;
    }

    void OnDialogConfirm(object sender, RoutedEventArgs e) => CloseDialog(DialogInput.Text.Trim());

    void OnDialogCancel(object sender, RoutedEventArgs e) => CloseDialog(null);

    async void OnNewProfile(object sender, RoutedEventArgs e)
    {
        var on = library.Installed.Count(m => m.Enabled && m.Source != Mods.ModSource.Missing);
        var text = on switch
        {
            0 => "No mods are switched on, so this profile starts empty. Switching to it turns every mod off.",
            1 => "Saves the 1 mod that's switched on, and its place in the load order.",
            _ => $"Saves the {on} mods that are switched on, and their load order.",
        };
        var name = await Ask("New profile", text, "Save profile", SuggestName(),
            check: n => library.FindProfile(n) is not null ? $"You already have a profile called {n}. Right-click it to update it instead." : null);
        if (name is { Length: > 0 }) library.SaveProfile(name);
    }

    string SuggestName()
    {
        for (var i = library.Profiles.Count + 1; ; i++)
        {
            var name = $"Profile {i}";
            if (library.FindProfile(name) is null) return name;
        }
    }

    static ProfileItem? ProfileOf(object sender) => ((FrameworkElement)sender).DataContext as ProfileItem;

    void OnProfileApply(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) library.ApplyProfile(item);
    }

    async void OnProfileUpdate(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } item) return;

        var on = library.Installed.Count(m => m.Enabled && m.Source != Mods.ModSource.Missing);
        var answer = await Ask($"Update {item.Name}?",
            $"It will hold the {on} {(on == 1 ? "mod" : "mods")} that {(on == 1 ? "is" : "are")} switched on now, in their current order, instead of its {item.Count}.", "Update");
        if (answer is not null) library.UpdateProfile(item);
    }

    async void OnProfileRename(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } item) return;

        var name = await Ask("Rename profile", "", "Rename", item.Name,
            check: n => library.FindProfile(n) is { } other && other != item ? $"You already have a profile called {n}." : null);
        if (name is { Length: > 0 }) library.RenameProfile(item, name);
    }

    async void OnProfileDelete(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } item) return;

        var answer = await Ask($"Delete {item.Name}?", "Only the profile is deleted. Your mods stay installed, and the ones that are on stay on.", "Delete", danger: true);
        if (answer is not null) library.DeleteProfile(item);
    }

    void OnProfileUp(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) library.MoveProfile(item, library.Profiles.IndexOf(item) - 1);
    }

    void OnProfileDown(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) library.MoveProfile(item, library.Profiles.IndexOf(item) + 1);
    }

    void OnProfileMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void RestorePlacement()
    {
        if (settings.Width < MinWidth || settings.Height < MinHeight) return;

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(settings.Left, settings.Top, settings.Width, settings.Height);
        if (!screen.IntersectsWith(bounds) || Rect.Intersect(screen, bounds).Width < 200) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (settings.Maximized) WindowState = WindowState.Maximized;
    }

    void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;

        settings.Left = bounds.Left;
        settings.Top = bounds.Top;
        settings.Width = bounds.Width;
        settings.Height = bounds.Height;
        settings.Maximized = WindowState == WindowState.Maximized;
        settings.Save();
    }

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
