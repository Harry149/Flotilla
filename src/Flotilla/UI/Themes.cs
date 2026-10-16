using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Flotilla.UI;

public static class Themes
{
    public const string Sonar = "Sonar";
    public const string Logbook = "Logbook";

    public static string Current { get; private set; } = Sonar;

    public static bool Caps => Current == Logbook;

    public static void Apply(Application app, string? name)
    {
        // shortcut: Logbook is hidden for now, so everyone gets Sonar. To bring it back, use `name == Logbook ? Logbook : Sonar` and restore the Theme choice in Settings.
        Current = Sonar;
        foreach (var part in new[] { Current, "Icons", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Theme/{part}.xaml") });
    }

    public static string Heading(string text) => Caps ? text.ToUpper(CultureInfo.CurrentCulture) : text;
}

public sealed class Upper : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string text ? Themes.Heading(text) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
