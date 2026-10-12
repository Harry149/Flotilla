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

    public bool ClearCacheBeforePlay { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallRequirements { get; set; } = true;
    public bool WarnGameVersion { get; set; } = true;
    public bool CheckGameLog { get; set; }
    public bool SendErrorReports { get; set; } = true;
    public bool CountMe { get; set; } = true;
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public string? DataFolder { get; set; }
    public string? GameVersion { get; set; }
    public DateTime LogChecked { get; set; }
    public List<ulong> Favourites { get; set; } = [];
    public Dictionary<string, bool> Votes { get; set; } = [];

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
