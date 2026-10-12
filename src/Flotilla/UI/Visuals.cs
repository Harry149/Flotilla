using System.Windows;
using System.Windows.Media;

namespace Flotilla.UI;

public static class Visuals
{
    public static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    public static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (DependencyObject? node = start; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is T match) return match;
        }
        return null;
    }
}
