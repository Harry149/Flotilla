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
using Flotilla.Mods;
using Flotilla.UI;

namespace Flotilla;

public partial class MainWindow : Window
{
    sealed record TourStep(string Icon, string Title, string Body, string[] Tips, string Page = "browse");

    static readonly TourStep[] Tour =
    [
        new("IconSonar", "Welcome to Flotilla",
            "Flotilla finds, installs and organises your UBOAT mods, and helps you play together and fix things when they break. This tour takes about two minutes, and the ? button brings it back whenever you like.",
            [
                "Everything you change goes straight into UBOAT's own mod list, so the game and its launcher see it too.",
                "Close UBOAT and its launcher before changing mods. Flotilla reminds you if you forget.",
            ]),
        new("IconBrowse", "Find mods in Browse",
            "Browse shows every mod on the UBOAT Workshop. The banner at the top shows mods we recommend.",
            [
                "Search by name or tag with Ctrl+F, and sort by popular, recently updated, newest or name.",
                "Hover a mod and press the star to save it. The Favourites filter finds them again, and Hide installed shows only what you don't have.",
                "A tick and a percentage on a card is how many sailors say it works on your UBOAT version.",
            ]),
        new("IconBox", "Every mod has a page",
            "Click a mod to open its page, then press Install. You don't need to sign in to Steam.",
            [
                "Needs these mods lists what it requires. Flotilla installs those with it and loads them first.",
                "What's new shows the author's latest change notes. Click the author's name to see all their mods, or a tag to see similar ones.",
                "Does it work on your UBOAT? Press It works or It's broken to help other sailors.",
                "A few mods need an account that owns UBOAT or a DLC. Use Open in Steam and subscribe there, and Flotilla picks them up.",
            ]),
        new("IconInstalled", "Manage them in Installed",
            "Installed lists every mod on this PC, in the order UBOAT loads them.",
            [
                "Flip the switch to turn a mod on or off. Mods you turn off stay installed. The button above the list switches every mod on, or every mod off.",
                "Drag the dots on the left to change the load order. When two mods change the same thing, the lower one wins.",
                "Double-click a mod to open its page. Right-click it for its folder, its Workshop page, its settings, a rollback or uninstalling.",
            ], Page: "installed"),
        new("IconGear", "Warnings, fixes and settings",
            "Flotilla keeps an eye on your mods so you don't have to.",
            [
                "A yellow line under a mod means it needs another mod, or was made for a different UBOAT version. Press Fix to install what it needs and put it in the right order.",
                "A red line means it broke in your last game, with the error right there.",
                "The gear on a mod opens its settings as simple switches and boxes, with the author's notes for each one.",
            ], Page: "installed"),
        new("IconLayers", "Save setups as profiles",
            "A profile remembers which mods are on and their order, so you can keep one setup for a full overhaul and another for a lighter game.",
            [
                "Turn on the mods you want, then press + next to Profiles in the sidebar.",
                "Click a profile to switch to it. If a mod in it isn't installed, Flotilla installs it for you.",
                "The tick shows which profile is on. Right-click a profile to share, update, rename, reorder or delete it.",
            ], Page: "installed"),
        new("IconExport", "Play together",
            "Co-op works best when everyone has exactly the same mods. Flotilla turns your setup into one short code.",
            [
                "Press Share on Installed. You get a code like FLT-8K2QR7, and a link is copied for you to paste into Discord.",
                "The link shows your mod list and opens Flotilla for anyone who clicks it.",
                "Friends press Use a code. Flotilla shows what's different first, then installs the same mods at the same versions in the same order.",
                "Joining someone? Use their code to see exactly where your mods differ before you connect.",
            ], Page: "installed"),
        new("IconSearch", "When something breaks",
            "Mods change all the time. Flotilla gives you a way back every time.",
            [
                "History keeps every setup you've had. Press Restore on one to go back to it, versions and all.",
                "Before an update, Flotilla keeps the old version. Right-click the mod and roll it back if the update broke something.",
                "Find a broken mod (in beta) switches half your mods off at a time. Play, say whether the problem's still there, and in a few rounds it names the mod.",
                "Turn on Check the game log in Settings, and when UBOAT closes Flotilla tells you which mod logged an error and offers to save the error log.",
            ], Page: "installed"),
        new("IconUpdate", "Staying up to date",
            "Flotilla checks the Workshop every time it starts.",
            [
                "A dot next to Installed means updates are ready. Update all gets them in one go.",
                "Pressing Update on one mod shows what's new since your version first.",
                "Mods you subscribe to in Steam show up here too, and Steam keeps those up to date. Flotilla updates itself too, and asks first.",
            ], Page: "installed"),
        new("IconGear", "Make it yours in Settings",
            "Settings holds the switches for everything Flotilla does in the background.",
            [
                "Clear UBOAT's cache when your mods change, so leftovers from old mods can't cause trouble and unchanged setups load faster.",
                "Install the mods a mod needs, warn about mods made for another UBOAT version, and check the game log after you play.",
                "Count me as a sailor, error reports and update checks can each be turned off. Export and Import a mod list file live under the ... button on Installed.",
            ], Page: "settings"),
        new("IconPlay", "Ready to dive",
            "Press Play UBOAT in the sidebar to start the game through Steam with your mods.",
            [
                "The top bar shows how many sailors have Flotilla open right now.",
                "F1 brings this tour back, F5 refreshes the Workshop list, and Ctrl+1 to Ctrl+4 jump between Browse, Installed, Settings and Credits.",
                "Credits has who makes Flotilla, and how to support it.",
            ]),
    ];

    readonly Library library = new();
    Settings settings => library.Settings;
    readonly Updater updater = new(Web.Client);
    readonly Sailors sailors = new(Web.Client);
    Update? update;
    string? handled;
    bool preparing;
    bool handingOver;
    GameAlert? problems;
    bool closeConfirmed;
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
        InstalledPage.HistoryRequested += () => Reveal(HistoryLayer, true);
        InstalledPage.FindBrokenRequested += () => _ = FindBrokenAsync();
        InstalledPage.BisectAnswered += stillBroken => _ = AnswerBisectAsync(stillBroken);
        InstalledPage.BestOrderRequested += () => _ = ShowBestOrderAsync();
        InstalledPage.ShareRequested += () => _ = ShareAsync(library.CurrentSetup(), "your setup");
        InstalledPage.UseCodeRequested += () => _ = UseCodeAsync(null);
        InstalledPage.SettingsRequested += OpenEditor;
        InstalledPage.UpdateRequested += mod => _ = UpdateWithNotesAsync(mod);
        Details.VoteRequested += (tile, works) => _ = library.VoteAsync(tile, works);
        Links.Received += OnLink;
        SettingsPage.TourRequested += () => ShowTour(0);
        SettingsPage.UpdateCheckRequested += () => _ = CheckForUpdateAsync(manual: true);
        SettingsPage.RestartRequested += OfferRestart;
        SettingsPage.CountMeChanged += () => _ = RecountAsync();
        library.PropertyChanged += OnLibraryChanged;
        Library.RestingChanged += OnRestingChanged;
        library.ProblemsFound += alert =>
        {
            problems = alert;
            Dispatcher.BeginInvoke(OfferProblems);
        };

        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.FirstOrDefault(a => a.StartsWith("--tab=")) is { } tab) TabFor(tab[6..]).IsChecked = true;
            if (args.Contains(Updater.Updated)) library.Status.Say($"Updated to {Updater.Current}.");
            if (!settings.TutorialSeen) ShowTour(0);
            _ = WatchForUpdatesAsync();
            _ = ShowDownloadsAsync();
            await library.StartAsync();
            if (library.IsBisecting)
            {
                InstalledTab.IsChecked = true;
                library.Status.Say("You're part way through finding a broken mod. Carry on from Installed.");
            }
            if (Links.Pending is { } link)
            {
                Links.Pending = null;
                OnLink(link);
            }
        };
        Activated += (_, _) => library.Rescan();
        SourceInitialized += (_, _) => FitToScreen();
        StateChanged += (_, _) => FitToScreen();
        Closing += OnClosing;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = DialogLayer.IsVisible ? DialogKey(e.Key)
            : TutorialLayer.IsVisible ? TourKey(e.Key)
            : (HistoryLayer.IsVisible || EditorLayer.IsVisible || OrderLayer.IsVisible) && e.Key == Key.Escape ? CloseLayers()
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
            case Key.D4 or Key.NumPad4 when control:
                CreditsTab.IsChecked = true;
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
        Details.ShowNews([]);
        Details.ShowGameVersion(library.GameVersionKnown);
        ShowView();
        _ = ShowRequirementsAsync(tile);
        _ = ShowNewsAsync(tile);
    }

    async Task ShowNewsAsync(ModTile tile)
    {
        var notes = await library.ChangeNotesAsync(tile.Id);
        if (open == tile) Details.ShowNews(notes);
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
        var credits = CreditsTab.IsChecked == true;
        Reveal(Details, open is not null);
        Reveal(InstalledPage, installed && open is null);
        Reveal(SettingsPage, settings && open is null);
        Reveal(CreditsPage, credits && open is null);
        Reveal(Browse, !installed && !settings && !credits && open is null);
        SearchBox.Visibility = (settings || credits) && open is null ? Visibility.Hidden : Visibility.Visible;
    }

    async void OfferRestart()
    {
        var answer = await Ask("Restart Flotilla?", "Flotilla uses the new data folder after it restarts.", "Restart now", cancel: "Later");
        if (answer is null) return;
        await RestartAsync();
    }

    async Task RestartAsync()
    {
        if (Environment.ProcessPath is not { } path) return;

        SavePlacement();
        closeConfirmed = true;
        handingOver = true;
        try
        {
            await HandOverAsync(System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, $"{Links.Handover} --tab={CurrentTab}") { UseShellExecute = true }));
        }
        catch (Win32Exception e)
        {
            closeConfirmed = handingOver = false;
            library.Status.Warn($"Couldn't restart Flotilla: {e.Message}");
        }
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
        TutorialTitle.Text = Themes.Heading(page.Title);
        TutorialBody.Text = page.Body;
        TutorialTips.ItemsSource = page.Tips;
        TutorialDots.ItemsSource = Enumerable.Range(0, Tour.Length).Select(i => i == tourStep).ToList();
        TutorialBack.Visibility = tourStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        TutorialNext.Content = tourStep == Tour.Length - 1 ? "Let's go" : "Next";

        TabFor(page.Page).IsChecked = true;
        CloseMod();

        if (!TutorialLayer.IsVisible) Reveal(TutorialLayer, true);
        TutorialNext.Focus();
    }

    void CloseTour()
    {
        TutorialLayer.Visibility = Visibility.Collapsed;
        tourStep = -1;
        Dispatcher.BeginInvoke(OfferUpdate);
        Dispatcher.BeginInvoke(OfferProblems);
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

    Task<string?> Ask(string title, string text, string confirm, string? input = null, bool danger = false, Func<string, string?>? check = null, string cancel = "Cancel", int longest = 40, bool pickHours = false)
    {
        DialogHours.Visibility = pickHours ? Visibility.Visible : Visibility.Collapsed;
        Hours24.IsChecked = true;
        DialogInput.MaxLength = longest;
        dialog?.TrySetResult(null);
        dialog = new TaskCompletionSource<string?>();
        validate = check;

        DialogTitle.Text = Themes.Heading(title);
        DialogText.Text = text;
        DialogScroll.ScrollToTop();
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
        Dispatcher.BeginInvoke(OfferProblems);
    }

    bool CloseLayers()
    {
        HistoryLayer.Visibility = Visibility.Collapsed;
        EditorLayer.Visibility = Visibility.Collapsed;
        OrderLayer.Visibility = Visibility.Collapsed;
        return true;
    }

    void OnCloseLayers(object sender, RoutedEventArgs e) => CloseLayers();

    void OnLayerBackground(object sender, MouseButtonEventArgs e) => CloseLayers();

    void OnPanelClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SnapshotItem item) return;
        CloseLayers();
        await library.RestoreAsync(item);
    }

    List<SettingsFile> editing = [];
    SettingsFile? editingFile;

    void OpenEditor(InstalledMod mod)
    {
        editing = [.. library.SettingsFilesOf(mod).Select(SettingsFile.Read).OfType<SettingsFile>()];
        if (editing.Count == 0)
        {
            if (library.SettingsFilesOf(mod).FirstOrDefault() is { } path) Shell.Open(path);
            else library.Status.Say($"{mod.Name} has no settings file Flotilla can read.");
            return;
        }

        EditorTitle.Text = Themes.Heading($"{mod.Name} settings");
        EditorFiles.ItemsSource = editing.Count > 1 ? editing : null;
        EditorFiles.Visibility = editing.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowEditorFile(editing[0]);
        Reveal(EditorLayer, true);
        if (editing.Count > 1) Dispatcher.BeginInvoke(() =>
        {
            if (Visuals.Find<RadioButton>(EditorFiles) is { } first) first.IsChecked = true;
        });
    }

    void ShowEditorFile(SettingsFile file)
    {
        editingFile = file;
        EditorAbout.Text = (file.About.Length > 0 ? file.About + " " : "")
            + "Some mods only read their settings when UBOAT starts, so restart the game if a change doesn't show.";
        EditorHint.Text = "";
        EditorFields.ItemsSource = file.Fields;
    }

    void OnEditorFile(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SettingsFile file) ShowEditorFile(file);
    }

    void OnEditorOpenFile(object sender, RoutedEventArgs e)
    {
        if (editingFile is not null) Shell.Open(editingFile.Path);
    }

    void OnEditorSave(object sender, RoutedEventArgs e)
    {
        foreach (var file in editing)
        {
            if (file.Fields.Where(f => f.Changed).Select(file.Problem).FirstOrDefault(p => p is not null) is { } problem)
            {
                ShowEditorFile(file);
                EditorHint.Text = problem;
                return;
            }
        }

        var saved = 0;
        foreach (var file in editing.Where(f => f.Fields.Any(field => field.Changed)))
        {
            try
            {
                file.Save();
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EditorHint.Text = $"Couldn't save {file.Name}: {ex.Message}";
                return;
            }
        }
        CloseLayers();
        library.Status.Say(saved == 0 ? "Nothing changed." : "Saved. Some mods only pick up changes the next time UBOAT starts.");
    }

    OrderPlan? plan;

    async Task ShowBestOrderAsync()
    {
        if (await library.BestOrderAsync() is not { } found) return;
        if (found.Moves == 0)
        {
            library.Status.Say("Your load order already looks right. Nothing needs to move.");
            return;
        }

        plan = found;
        OrderSummary.Text = (found.Moves == 1 ? "1 mod moves." : $"{found.Moves} mods move.")
            + " Only mods that are switched on are sorted. Nothing changes until you press Use this order, and your current order stays in History.";
        OrderRows.ItemsSource = found.Rows;
        Reveal(OrderLayer, true);
    }

    void OnUseOrder(object sender, RoutedEventArgs e)
    {
        CloseLayers();
        if (plan is { } chosen) library.UseOrder(chosen);
        plan = null;
    }

    async Task AnswerBisectAsync(bool stillBroken)
    {
        if (library.BisectUnplayed && await Ask("You haven't played this test yet",
                "Flotilla hasn't seen UBOAT start since it set up this test. Answering without playing it can point the search at the wrong mod.",
                "Answer anyway", cancel: "Not yet") is null)
            return;
        library.AnswerBisect(stillBroken);
    }

    async Task FindBrokenAsync()
    {
        if (library.IsBisecting) return;
        var on = library.Installed.Count(m => m.Enabled && m.Source != Mods.ModSource.Missing);
        if (on < 2)
        {
            library.Status.Warn(on == 0
                ? "No mods are switched on, so there's nothing to search. Switch on the mods you play with first."
                : "Only one mod is switched on, so if something's wrong, it's that one. Switch it off and play to check.");
            return;
        }

        var rounds = (int)Math.Ceiling(Math.Log2(on));
        var answer = await Ask("Find a broken mod (beta)",
            $"If something's wrong in UBOAT and the log doesn't say which mod did it, Flotilla can find it in about {rounds} {(rounds == 1 ? "test" : "tests")} with your {on} mods.\n\n"
            + "Each test switches half of the suspects off. You play, check whether the problem still happens, and tell Flotilla. "
            + "Mods another mod needs, and patches with the mod they patch, stay on together, so nothing is tested half-installed. Your setup is saved first, and Stop puts everything back the way it was.\n\n"
            + "This is still in beta. If it points at the wrong mod, tell me so I can improve it.",
            "Start", cancel: "Not now");
        if (answer is not null && library.StartBisect()) InstalledTab.IsChecked = true;
    }

    async Task ShareAsync(IReadOnlyList<Mods.PackEntry> setup, string what)
    {
        if (!Sailors.Enabled) return;
        if (setup.Count == 0)
        {
            library.Status.Warn("There's nothing to share. Switch on some mods first.");
            return;
        }
        if (await Ask("Share your setup", $"Makes a code and a link for {what}. How long should it work?", "Make a code", pickHours: true) is null) return;
        var hours = Hours48.IsChecked == true ? 48 : Hours72.IsChecked == true ? 72 : 24;

        library.Status.Work("Sharing", $"Making a code for {what}");
        var shared = await library.ShareAsync(setup, hours);
        library.Status.Idle();
        if (shared is null) return;
        var code = shared.Code;

        var link = $"{Sailors.Api}/s/{code}";
        TryCopy(link);
        var until = shared.Expires is { } expires ? $"It works until {expires.ToLocalTime():ddd d MMM, HH:mm}. After that, make a new one.\n\n" : "";
        var answer = await Ask($"Share code: {ShareCodes.Display(code)}",
            $"The link is copied, so you can paste it straight into Discord. It shows the mods and opens Flotilla:\n\n{link}\n\n{until}"
            + $"Friends can also press Use a code in Flotilla and type {ShareCodes.Display(code)}. It installs the same mods at the same versions, in the same order.",
            "Copy the code", cancel: "Done");
        if (answer is not null && TryCopy(ShareCodes.Display(code))) library.Status.Say($"Copied {ShareCodes.Display(code)}.");
    }

    static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    async Task UseCodeAsync(string? given)
    {
        if (!Sailors.Enabled) return;
        var input = given ?? await Ask("Use a code", "Paste a friend's share code or link. Flotilla shows what's different before changing anything.",
            "Look it up", "", check: text => ShareCodes.Normalise(text) is null ? "That doesn't look like a Flotilla code. They look like FLT-8K2QR7." : null, longest: 300);
        if (input is null || ShareCodes.Normalise(input) is not { } code) return;

        library.Status.Work("Reading the code", ShareCodes.Display(code));
        var setup = await library.ReadShareAsync(code);
        library.Status.Idle();
        if (setup is null)
        {
            library.Status.Warn($"No setup has the code {ShareCodes.Display(code)}. It may have run out, since codes last 1 to 3 days. Ask for a new one.");
            return;
        }

        var comparison = library.CompareSetup(setup);
        var same = comparison.StartsWith("You already");
        var answer = await Ask($"Setup {ShareCodes.Display(code)}: {setup.Count} {(setup.Count == 1 ? "mod" : "mods")}",
            same ? comparison : $"{comparison}\n\nYour current setup is saved in History first, so you can go back to it.",
            same ? "Okay" : "Match this setup", cancel: same ? "Close" : "Not now");
        if (answer is null || same) return;

        InstalledTab.IsChecked = true;
        if (!await library.ApplySetupAsync(setup, "Matched", $"Matched {ShareCodes.Display(code)}")) return;
        var name = $"Shared {ShareCodes.Display(code)}";
        if (library.FindProfile(name) is null) library.SaveProfile(name);
    }

    void OnLink(string link)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (ShareCodes.Normalise(link) is { } code) _ = UseCodeAsync(code);
    }

    async Task UpdateWithNotesAsync(InstalledMod mod)
    {
        if (mod.Tile is not { } tile) return;
        if (tile.State != TileState.Outdated)
        {
            library.Install(tile);
            return;
        }

        var since = library.InstalledOn(tile.Id);
        var notes = (await library.ChangeNotesAsync(tile.Id)).Where(n => n.Text.Length > 0 && (since is null || n.Time > since)).Take(5).ToList();
        if (notes.Count == 0)
        {
            library.Install(tile);
            return;
        }

        var text = string.Join("\n\n", notes.Select(n => $"{n.Time.ToLocalTime():d MMM}: {(n.Text.Length > 300 ? n.Text[..300].TrimEnd() + "…" : n.Text)}"));
        var answer = await Ask($"Update {mod.Name}?", $"What's new since your version:\n\n{text}", "Update", cancel: "Not now");
        if (answer is not null) library.Install(tile);
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!closeConfirmed && settings.CheckGameLog && Library.GameIsRunning())
        {
            e.Cancel = true;
            var answer = await Ask("Close Flotilla while UBOAT is running?",
                "Flotilla checks UBOAT's log for mod errors when the game closes. If you close Flotilla now, it can't tell you about this game's errors until you open it again.",
                "Close anyway", cancel: "Keep Flotilla open");
            if (answer is null) return;

            closeConfirmed = true;
            Close();
            return;
        }

        if (handingOver) return;
        SavePlacement();
        SayGoodbye();
    }

    async void OfferProblems()
    {
        if (problems is not { } alert || TutorialLayer.IsVisible || DialogLayer.IsVisible) return;
        problems = null;

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        var title = alert.Mods.Count == 1 ? $"Flotilla found an error with {alert.Mods[0].Mod}" : $"Flotilla found errors with {alert.Mods.Count} mods";
        var lines = alert.Mods.Take(4).Select(m => alert.Mods.Count == 1 ? m.Error : $"{m.Mod}: {m.Error}").ToList();
        if (alert.Mods.Count > 4) lines.Add($"…and {alert.Mods.Count - 4} more.");
        var text = $"Your last game of UBOAT logged this:\n\n{string.Join("\n\n", lines)}"
            + (alert.Warnings > 0 ? $"\n\nThe error log also has {alert.Warnings} {(alert.Warnings == 1 ? "warning" : "warnings")} from {(alert.Mods.Count == 1 ? "it" : "them")}." : "")
            + "\n\nThe mod is marked in Installed. Switching it off, updating it, or sending the error log to its author usually sorts it out.";

        var answer = await Ask(title, text, "Save error log to Downloads", cancel: "Okay!");
        if (answer is null) return;

        try
        {
            var path = Path.Combine(KnownFolders.Downloads, $"Flotilla error log {DateTime.Now:yyyy-MM-dd HHmm}.txt");
            await File.WriteAllTextAsync(path, alert.Report);
            Shell.Select(path);
            library.Status.Say($"Saved the error log to {Path.GetFileName(path)} in Downloads.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            library.Status.Warn($"Couldn't save the error log: {e.Message}");
        }
    }

    async Task ShowDownloadsAsync()
    {
        if (Sailors.Enabled)
        {
            _ = SailAsync();
            return;
        }
        try
        {
            if (await updater.DownloadsAsync() is not { } count || count <= 0) return;
            DownloadsText.Text = count == 1 ? "1 download" : $"{count:N0} downloads";
            Downloads.ToolTip = "Times Flotilla has been downloaded from GitHub, counting updates";
            Downloads.Visibility = Visibility.Visible;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
    }

    async Task SailAsync()
    {
        settings.Save();
        using var timer = new PeriodicTimer(Sailors.Heartbeat);
        do
        {
            try
            {
                ShowSailors(settings.CountMe ? await sailors.PingAsync(settings.InstallId) : await sailors.CountAsync());
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
        } while (await timer.WaitForNextTickAsync());
    }

    async Task RecountAsync()
    {
        if (!Sailors.Enabled) return;
        try
        {
            ShowSailors(settings.CountMe ? await sailors.PingAsync(settings.InstallId) : await sailors.ByeAsync(settings.InstallId));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
    }

    void ShowSailors(long? count)
    {
        if (count is not > 0) return;

        awake = ((Geometry)FindResource("IconUsers"), count == 1 ? "Flotilla is assisting 1 sailor" : $"Flotilla is assisting {count:N0} sailors",
            "People with Flotilla open right now. You can stop being counted in Settings.");
        if (!Library.Resting) ShowTopBar(awake.Value);
    }

    // What the top bar shows when UBOAT isn't running, kept aside while Flotilla rests.
    (Geometry Icon, string Text, object Tip)? awake;

    void ShowTopBar((Geometry Icon, string Text, object Tip) shown)
    {
        DownloadsIcon.Data = shown.Icon;
        DownloadsText.Text = shown.Text;
        Downloads.ToolTip = shown.Tip;
        Downloads.Visibility = Visibility.Visible;
    }

    void OnRestingChanged()
    {
        if (Library.Resting)
        {
            if (Downloads.Visibility == Visibility.Visible) awake ??= (DownloadsIcon.Data, DownloadsText.Text, Downloads.ToolTip);
            ShowTopBar(((Geometry)FindResource("IconSonar"), "Flotilla is resting while you play",
                "Flotilla pauses its own background work while UBOAT runs. Change this in Settings."));
        }
        else if (awake is { } shown) ShowTopBar(shown);
        else Downloads.Visibility = Visibility.Collapsed;
    }

    void SayGoodbye()
    {
        if (!Sailors.Enabled || !settings.CountMe) return;
        try
        {
            Task.Run(() => sailors.ByeAsync(settings.InstallId)).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException) { }
    }

    async Task WatchForUpdatesAsync()
    {
        _ = Updater.CleanUpAsync();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            if (settings.CheckForUpdates && !Library.Resting) await CheckForUpdateAsync();
        } while (await timer.WaitForNextTickAsync());
    }

    async Task CheckForUpdateAsync(bool manual = false)
    {
        if (preparing) return;

        Update? found;
        try
        {
            found = await updater.CheckAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (manual) library.Status.Warn($"Couldn't check for updates: {e.Message}");
            return;
        }

        if (found is null)
        {
            Updater.CleanUpDownload();
            if (manual) library.Status.Say($"You have the latest Flotilla, {Updater.Current}.");
            return;
        }
        if (!manual && found.Version == handled) return;

        if (update?.Version != found.Version && Updater.CanReplaceItself)
        {
            preparing = true;
            try
            {
                await updater.DownloadAsync(found, new Progress<double>(p =>
                {
                    if (manual) library.Status.Work("Updating Flotilla", $"Downloading {found.Version}, {p * 100:0}%", p);
                }));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                if (manual) library.Status.Warn($"Couldn't download Flotilla {found.Version}: {e.Message}");
            }
            finally
            {
                preparing = false;
                if (manual) library.Status.Idle();
            }
        }

        update = found;
        if (manual && Library.GameIsRunning()) library.Status.Say($"Flotilla {found.Version} is ready. It offers the update once UBOAT closes.");
        OfferUpdate();
    }

    async void OfferUpdate()
    {
        if (update is not { } offer || TutorialLayer.IsVisible || DialogLayer.IsVisible || Library.GameIsRunning()) return;
        update = null;
        handled = offer.Version;

        var notes = offer.Notes.Length > 420 ? offer.Notes[..420].TrimEnd() + "…" : offer.Notes;
        var text = $"You have {Updater.Current}. {(Updater.CanReplaceItself ? "Updating takes a moment, and Flotilla reopens where you left it." : "This copy can't update itself, so the download page opens.")}";
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
            if (library.Busy)
            {
                library.Status.Say("Flotilla updates as soon as your mods finish installing.");
                while (library.Busy) await Task.Delay(500);
            }

            var downloaded = await updater.DownloadAsync(offer, new Progress<double>(p =>
                library.Status.Work("Updating Flotilla", $"Downloading {offer.Version}, {p * 100:0}%", p)));
            SavePlacement();
            closeConfirmed = true;
            handingOver = true;
            await HandOverAsync(Updater.Install(downloaded, $"{Links.Handover} {Updater.Updated} --tab={CurrentTab}"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            closeConfirmed = handingOver = false;
            library.Status.Idle();
            library.Status.Warn($"Couldn't update Flotilla: {e.Message} You can download it from the releases page instead.");
            Shell.Open(offer.Page);
        }
    }

    static async Task HandOverAsync(System.Diagnostics.Process? next)
    {
        using (next)
        {
            for (var i = 0; next is not null && i < 100 && !next.HasExited; i++)
            {
                next.Refresh();
                if (next.MainWindowHandle != IntPtr.Zero)
                {
                    await Task.Delay(500);
                    break;
                }
                await Task.Delay(200);
            }
        }
        Application.Current.Shutdown();
    }

    string CurrentTab => InstalledTab.IsChecked == true ? "installed" : SettingsTab.IsChecked == true ? "settings" : CreditsTab.IsChecked == true ? "credits" : "browse";

    RadioButton TabFor(string name) => name switch { "installed" => InstalledTab, "settings" => SettingsTab, "credits" => CreditsTab, _ => BrowseTab };

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
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
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

    void OnProfileShare(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } item) _ = ShareAsync(library.ProfileSetup(item), item.Name);
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
