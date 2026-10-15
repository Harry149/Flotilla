using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flotilla.UI;

public static class ScrollFade
{
    static readonly Brush Mask = CreateMask();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollFade), new PropertyMetadata(false, OnEnabled));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    static void OnEnabled(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ScrollViewer viewer || e.NewValue is not true) return;
        viewer.ScrollChanged += (_, _) => viewer.OpacityMask = viewer.VerticalOffset > 0.5 ? Mask : null;
    }

    static Brush CreateMask()
    {
        var mask = new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(0, 24)) { MappingMode = BrushMappingMode.Absolute };
        mask.Freeze();
        return mask;
    }
}
