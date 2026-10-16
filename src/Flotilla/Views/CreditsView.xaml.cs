using System.Windows;
using System.Windows.Controls;
using Flotilla.UI;

namespace Flotilla.Views;

public partial class CreditsView : UserControl
{
    public CreditsView()
    {
        InitializeComponent();
        VersionText.Text = $"Flotilla {Updater.Current}, a mod manager for UBOAT";
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || DataContext is not Library { Settings: var settings }) return;
            StatInstalled.Text = settings.ModsInstalled.ToString("N0");
            StatUpdated.Text = settings.UpdatesApplied.ToString("N0");
            StatLaunched.Text = settings.GamesLaunched.ToString("N0");
            StatErrors.Text = settings.ErrorsCaught.ToString("N0");
        };
    }

    void OnGitHub(object sender, RoutedEventArgs e) => Shell.Open("https://github.com/Harry149");

    void OnPayPal(object sender, RoutedEventArgs e) => Shell.Open("https://paypal.me/QuinnZoie");

    void OnPingu(object sender, RoutedEventArgs e) => Shell.Open("https://buymeacoffee.com/pingu565");
}
