using System.Windows;
using System.Windows.Automation;
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
    public event Action<string>? TagRequested;
    public event Action<ModTile>? AuthorRequested;
    public event Action<ModTile>? OpenRequested;

    public void ShowRequirements(IReadOnlyList<RequirementRow> rows)
    {
        NeedsList.ItemsSource = rows;
        Needs.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnNeed(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RequirementRow { Tile: { } tile }) OpenRequested?.Invoke(tile);
    }

    void OnAuthor(object sender, RoutedEventArgs e)
    {
        if (DataContext is ModTile tile) AuthorRequested?.Invoke(tile);
    }

    public string BackLabel
    {
        get => BackText.Text;
        set
        {
            BackText.Text = value;
            AutomationProperties.SetName(BackButton, $"Back to {value}");
        }
    }

    void OnTag(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is string tag) TagRequested?.Invoke(tag);
    }

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
