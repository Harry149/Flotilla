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
    }

    void OnGitHub(object sender, RoutedEventArgs e) => Shell.Open("https://github.com/Harry149");

    void OnPayPal(object sender, RoutedEventArgs e) => Shell.Open("https://paypal.me/QuinnZoie");
}
