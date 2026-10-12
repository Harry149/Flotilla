using System.IO;
using System.Text.Json;

namespace Flotilla;

public sealed class Settings
{
    static readonly string Location = Path.Combine(Paths.AppData, "settings.json");

    public bool TutorialSeen { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }

    public static Settings Load()
    {
        try
        {
            return File.Exists(Location) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Location)) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Location, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
