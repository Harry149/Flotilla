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
        Order.Loaded += (_, _) =>
        {
            if (Visuals.Find<ScrollViewer>(Order) is { } viewer) ScrollFade.SetEnabled(viewer, true);
        };
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is Library library)
            {
                HideOff.IsChecked = library.Settings.HideSwitchedOff;
                library.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName is nameof(Library.BisectTitle)) ShowBisectButtons();
                    if (change.PropertyName is nameof(Library.InstalledSummary)) ApplyFilter();
                };
                ApplyFilter();
            }
        };
    }

    public event Action? BrowseRequested;
    public event Action<ModTile>? OpenRequested;
    public event Action? HistoryRequested;
    public event Action? FindBrokenRequested;
    public event Action? BestOrderRequested;
    public event Action? ShareRequested;
    public event Action? UseCodeRequested;
    public event Action<InstalledMod>? SettingsRequested;
    public event Action<InstalledMod>? UpdateRequested;

    void OnHistory(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke();

    void OnFindBroken(object sender, RoutedEventArgs e) => FindBrokenRequested?.Invoke();

    void OnShare(object sender, RoutedEventArgs e) => ShareRequested?.Invoke();

    void OnUseCode(object sender, RoutedEventArgs e) => UseCodeRequested?.Invoke();

    void OnMore(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is not { } menu) return;
        menu.DataContext = DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void OnModSettings(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { } mod) SettingsRequested?.Invoke(mod);
    }

    async void OnRollBack(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { } mod) await Library.RollBackAsync(mod);
    }

    void OnBisectPlay(object sender, RoutedEventArgs e) => Library.PlayGame();

    public event Action<bool>? BisectAnswered;

    void OnStillBroken(object sender, RoutedEventArgs e) => BisectAnswered?.Invoke(true);

    void OnFixed(object sender, RoutedEventArgs e) => BisectAnswered?.Invoke(false);

    void OnUndoBisect(object sender, RoutedEventArgs e) => Library.UndoBisect();

    void OnKeepOff(object sender, RoutedEventArgs e) => Library.StopBisect(turnOffCulprit: true);

    void OnStopBisect(object sender, RoutedEventArgs e) => Library.StopBisect();

    void ShowBisectButtons()
    {
        if (DataContext is not Library library) return;
        var found = library.BisectFound;
        StillBroken.Visibility = Fixed.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        KeepOff.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
    }

    Library Library => (Library)DataContext;

    string query = "";

    bool Filtering => query.Length > 0;

    bool Hiding => HideOff.IsChecked == true;

    public void Filter(string text)
    {
        query = text.Trim();
        ApplyFilter();
    }

    void OnHideOff(object sender, RoutedEventArgs e)
    {
        Library.Settings.HideSwitchedOff = Hiding;
        Library.Settings.Save();
        ApplyFilter();
    }

    void ApplyFilter()
    {
        if (drag is not null) return;

        var text = query;
        var hiding = Hiding;
        Order.Items.Filter = text.Length == 0 && !hiding ? null
            : item => item is InstalledMod mod && (!hiding || mod.Enabled)
                && (text.Length == 0 || mod.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || mod.Key.Contains(text, StringComparison.OrdinalIgnoreCase));

        var hidden = hiding ? Library.Installed.Count(m => !m.Enabled) : 0;
        Hint.Text = hidden > 0
            ? hidden == 1 ? "1 switched-off mod hidden." : $"{hidden} switched-off mods hidden."
            : Library.Installed.Count > 1 ? "Loads top to bottom. The lower mod wins when two change the same thing." : "";

        var none = Order.Items.Count == 0 && Library.Installed.Count > 0;
        NoMatchTitle.Text = Themes.Heading(text.Length > 0 ? $"No installed mod matches “{text}”" : "Every mod is switched off");
        NoMatchText.Text = text.Length > 0 ? "Try part of the name, or clear the search with Esc." : "Switch some on, or turn off Hide switched-off mods to see them.";
        NoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnToggle(object sender, RoutedEventArgs e) => Library.Save();

    void OnFix(object sender, RoutedEventArgs e)
    {
        if (ModOf(sender) is { } mod) Library.Fix(mod);
    }

    void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (System.IO.File.Exists(Paths.GameLog)) Shell.Open(Paths.GameLog);
        else Library.Status.Say("There's no game log yet. UBOAT writes one when it runs.");
    }

    void OnToggleAll(object sender, RoutedEventArgs e) => Library.ToggleAll();

    void OnBestOrder(object sender, RoutedEventArgs e) => BestOrderRequested?.Invoke();

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
        if (((FrameworkElement)sender).DataContext is InstalledMod { Tile: { } } mod) UpdateRequested?.Invoke(mod);
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

        var index = Order.Items.IndexOf(mod);
        var target = e.SystemKey switch
        {
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.Home => 0,
            Key.End => Order.Items.Count - 1,
            _ => -1,
        };
        if (target < 0 || target >= Order.Items.Count) return;

        Library.Move(mod, Library.Installed.IndexOf((InstalledMod)Order.Items[target]));
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
        var from = Order.Items.IndexOf(mod);
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

        var last = Order.Items.Count - 1;
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
        var target = d.Target != d.From ? Library.Installed.IndexOf((InstalledMod)Order.Items[d.Target]) : -1;
        drag = null;

        if (target >= 0) Library.Move(d.Mod, target);
    }
}
