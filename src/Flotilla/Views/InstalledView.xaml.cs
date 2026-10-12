using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Flotilla.Mods;
using Flotilla.UI;
using Microsoft.Win32;

namespace Flotilla.Views;

public partial class InstalledView : UserControl
{
    const double ScrollZone = 36;
    const double ScrollSpeed = 12;

    static readonly Duration SlideTime = new(TimeSpan.FromMilliseconds(150));
    static readonly IEasingFunction Ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    readonly DispatcherTimer edgeScroll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    InstalledMod? pressed;
    Point pressedAt;
    ScrollViewer? scroller;
    Drag? drag;

    public InstalledView()
    {
        InitializeComponent();
        edgeScroll.Tick += (_, _) => EdgeScroll();
    }

    public event Action? BrowseRequested;
    public event Action<ModTile>? OpenRequested;

    Library Library => (Library)DataContext;

    bool Filtering => Order.Items.Filter is not null;

    public void Filter(string text)
    {
        text = text.Trim();
        Order.Items.Filter = text.Length == 0 ? null
            : item => item is InstalledMod mod && (mod.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || mod.Key.Contains(text, StringComparison.OrdinalIgnoreCase));

        var none = text.Length > 0 && Order.Items.Count == 0;
        NoMatchTitle.Text = $"No installed mod matches “{text}”";
        NoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnToggle(object sender, RoutedEventArgs e) => Library.Save();

    void OnAllOn(object sender, RoutedEventArgs e) => Library.SetAll(true);

    void OnAllOff(object sender, RoutedEventArgs e) => Library.SetAll(false);

    static InstalledMod? ModOf(object sender) => ((FrameworkElement)sender).DataContext as InstalledMod;

    void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || Visuals.Ancestor<ButtonBase>(source) is not null) return;
        if (Visuals.Ancestor<ListBoxItem>(source)?.DataContext is InstalledMod { Tile: { } tile }) OpenRequested?.Invoke(tile);
    }

    void OnOpenPage(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { Tile: { } tile }) OpenRequested?.Invoke(tile);
    }

    void OnOpenModFolder(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { Folder.Path: { } path }) Shell.Open(path);
    }

    void OnOpenWorkshop(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { WorkshopId: { } id }) Shell.OpenInSteam(id);
    }

    void OnRemoveNow(object sender, RoutedEventArgs e) => OnRemove(sender, e);

    void OnBrowse(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke();

    void OnOpenFolder(object sender, RoutedEventArgs e) => Library.OpenModsFolder();

    void OnUpdateAll(object sender, RoutedEventArgs e) => Library.UpdateAll();

    const string ListFilter = "Mod lists (*.txt)|*.txt|All files (*.*)|*.*";

    void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Export mods", Filter = ListFilter, FileName = "UBOAT mods.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Library.Export(dialog.FileName);
    }

    async void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Import mods", Filter = ListFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await Library.ImportAsync(dialog.FileName);
    }

    void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledMod { Tile: { } tile }) Library.Install(tile);
    }

    void OnMoveTop(object sender, RoutedEventArgs e) => MoveTo(sender, 0);

    void OnMoveBottom(object sender, RoutedEventArgs e) => MoveTo(sender, int.MaxValue);

    void MoveTo(object sender, int index)
    {
        if (((FrameworkElement)sender).DataContext is InstalledMod mod) Library.Move(mod, index);
    }

    async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not InstalledMod mod) return;

        if (mod.Confirming || mod.Source is ModSource.Steam or ModSource.Missing)
        {
            mod.Confirming = false;
            Library.Remove(mod);
            return;
        }

        mod.Confirming = true;
        await Task.Delay(TimeSpan.FromSeconds(4));
        mod.Confirming = false;
    }

    void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.System || Filtering || Order.SelectedItem is not InstalledMod mod) return;

        var index = Library.Installed.IndexOf(mod);
        var target = e.SystemKey switch
        {
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.Home => 0,
            Key.End => Library.Installed.Count - 1,
            _ => -1,
        };
        if (target < 0 || target >= Library.Installed.Count) return;

        Library.Move(mod, target);
        Order.SelectedItem = mod;
        (Order.ItemContainerGenerator.ContainerFromItem(mod) as ListBoxItem)?.Focus();
        e.Handled = true;
    }

    sealed class Drag(InstalledMod mod, ListBoxItem item, int from, double step, double startY)
    {
        public InstalledMod Mod { get; } = mod;
        public ListBoxItem Item { get; } = item;
        public int From { get; } = from;
        public double Step { get; } = step;
        public double StartY { get; } = startY;
        public int Target { get; set; } = from;
        public bool Settling { get; set; }
    }

    void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (drag is not null) return;
        if (Filtering)
        {
            Library.Status.Say("Clear the search to change the load order.");
            e.Handled = true;
            return;
        }

        var grip = (FrameworkElement)sender;
        pressed = grip.DataContext as InstalledMod;
        pressedAt = e.GetPosition(Order);
        grip.CaptureMouse();
        e.Handled = true;
    }

    void OnGripMove(object sender, MouseEventArgs e)
    {
        if (drag is { Settling: false })
        {
            Follow();
            return;
        }
        if (pressed is null || drag is not null) return;
        if (Math.Abs(e.GetPosition(Order).Y - pressedAt.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var mod = pressed;
        pressed = null;
        Lift(mod);
    }

    void OnGripUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    void OnGripLost(object sender, MouseEventArgs e)
    {
        pressed = null;
        Settle();
    }

    double PointerY() => Mouse.GetPosition(Order).Y + (scroller?.VerticalOffset ?? 0);

    void Lift(InstalledMod mod)
    {
        scroller ??= Visuals.Find<ScrollViewer>(Order);
        var from = Library.Installed.IndexOf(mod);
        if (from < 0 || Order.ItemContainerGenerator.ContainerFromItem(mod) is not ListBoxItem item) return;

        drag = new Drag(mod, item, from, item.ActualHeight + item.Margin.Top + item.Margin.Bottom, PointerY());
        Panel.SetZIndex(item, 1);
        item.RenderTransform = new TranslateTransform();
        item.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black };
        edgeScroll.Start();
    }

    void Follow()
    {
        if (drag is not { } d) return;

        var last = Library.Installed.Count - 1;
        var offset = Math.Clamp(PointerY() - d.StartY, -d.From * d.Step, (last - d.From) * d.Step);
        ((TranslateTransform)d.Item.RenderTransform).Y = offset;

        var target = Math.Clamp((int)Math.Round(d.From + offset / d.Step), 0, last);
        if (target == d.Target) return;
        d.Target = target;

        for (var i = 0; i <= last; i++)
        {
            if (i == d.From || Order.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row) continue;

            var shift = i > d.From && i <= target ? -d.Step
                : i < d.From && i >= target ? d.Step
                : 0;
            if (row.RenderTransform is not TranslateTransform slide) row.RenderTransform = slide = new TranslateTransform();
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(shift, SlideTime) { EasingFunction = Ease });
        }
    }

    void EdgeScroll()
    {
        if (drag is not { Settling: false } || scroller is null) return;

        var y = Mouse.GetPosition(Order).Y;
        var speed = y < ScrollZone ? -ScrollSpeed : y > Order.ActualHeight - ScrollZone ? ScrollSpeed : 0;
        if (speed == 0) return;

        scroller.ScrollToVerticalOffset(scroller.VerticalOffset + speed);
        scroller.UpdateLayout();
        Follow();
    }

    void Settle()
    {
        if (drag is not { Settling: false } d) return;

        d.Settling = true;
        edgeScroll.Stop();
        var place = new DoubleAnimation((d.Target - d.From) * d.Step, SlideTime) { EasingFunction = Ease };
        place.Completed += (_, _) => Place(d);
        d.Item.RenderTransform.BeginAnimation(TranslateTransform.YProperty, place);
    }

    void Place(Drag d)
    {
        foreach (var mod in Library.Installed)
        {
            if (Order.ItemContainerGenerator.ContainerFromItem(mod) is not ListBoxItem row) continue;
            row.RenderTransform = Transform.Identity;
            row.Effect = null;
            Panel.SetZIndex(row, 0);
        }
        drag = null;

        if (d.Target != d.From) Library.Move(d.Mod, d.Target);
    }
}
