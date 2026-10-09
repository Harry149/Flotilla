using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Flotilla.Text;
using Flotilla.UI;

namespace Flotilla.Views;

public partial class ModPage : UserControl
{
    ulong shown;

    public ModPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowDescription();
    }

    public event Action? Closed;
    public event Action<ModTile>? InstallRequested;

    void ShowDescription()
    {
        if (DataContext is not ModTile tile || tile.Id == shown) return;

        shown = tile.Id;
        Description.Document = BbDocument.Build(tile.Item.Description);
        Scroller.ScrollToTop();
    }

    void OnDescriptionWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
    }

    void OnBack(object sender, RoutedEventArgs e) => Closed?.Invoke();

    void OnInstall(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModTile tile) InstallRequested?.Invoke(tile);
    }

    void OnOpenInSteam(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModTile tile) Shell.OpenInSteam(tile.Id);
    }
}
