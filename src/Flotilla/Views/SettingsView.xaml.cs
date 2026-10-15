using System.IO;
using System.Windows;
using System.Windows.Controls;
using Flotilla.UI;
using Microsoft.Win32;

namespace Flotilla.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible || DataContext is not Library library) return;

            SonarTheme.IsChecked = Themes.Current == Themes.Sonar;
            LogbookTheme.IsChecked = Themes.Current == Themes.Logbook;
            VersionText.Text = $"You have Flotilla {Updater.Current}. New versions come from GitHub, and Flotilla asks before installing one.";
            FolderText.Text = library.Settings.DataFolder is { Length: > 0 } custom
                ? $"{custom}{(custom == Paths.Game ? "" : " (used after Flotilla restarts)")}"
                : Paths.Game;
            ResetFolder.Visibility = library.Settings.DataFolder is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

            ClearNow.Content = "Clear now";
            var size = await Task.Run(library.CacheSize);
            ClearNow.Content = size > 0 ? $"Clear now ({size / (1024.0 * 1024):0} MB)" : "Clear now (empty)";
        };
    }

    public event Action? TourRequested;
    public event Action? UpdateCheckRequested;
    public event Action? RestartRequested;
    public event Action? CountMeChanged;
    public event Action? ThemeChanged;

    void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string theme } || theme == Themes.Current) return;
        Library.Settings.Theme = theme;
        Library.Settings.Save();
        ThemeChanged?.Invoke();
    }

    Library Library => (Library)DataContext;

    void OnChanged(object sender, RoutedEventArgs e) => Library.SettingsChanged();

    void OnCountMe(object sender, RoutedEventArgs e)
    {
        Library.SettingsChanged();
        CountMeChanged?.Invoke();
    }

    void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (Library.ClearCache()) ClearNow.Content = "Clear now (empty)";
    }

    void OnCheckUpdates(object sender, RoutedEventArgs e) => UpdateCheckRequested?.Invoke();

    void OnTour(object sender, RoutedEventArgs e) => TourRequested?.Invoke();

    void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose UBOAT's data folder", InitialDirectory = Paths.Game };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var folder = dialog.FolderName;
        if (!File.Exists(Path.Combine(folder, "modlist.txt")) && !Directory.Exists(Path.Combine(folder, "Mods")) && !Directory.Exists(Path.Combine(folder, "Saves")))
        {
            Library.Status.Warn("That doesn't look like UBOAT's data folder. It should have Mods, Saves or modlist.txt in it.");
            return;
        }
        UseFolder(folder);
    }

    void OnResetFolder(object sender, RoutedEventArgs e) => UseFolder(null);

    void UseFolder(string? folder)
    {
        Library.Settings.DataFolder = folder;
        Library.SettingsChanged();
        RestartRequested?.Invoke();
    }

    void OnOpenData(object sender, RoutedEventArgs e) => Shell.Open(Paths.Game);

    void OnOpenMods(object sender, RoutedEventArgs e) => Library.OpenModsFolder();

    void OnOpenFlotilla(object sender, RoutedEventArgs e) => Shell.Open(Paths.AppData);

    void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Paths.GameLog)) Shell.Open(Paths.GameLog);
        else Library.Status.Say("There's no game log yet. UBOAT writes one when it runs.");
    }
}
