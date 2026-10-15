using System.IO;

namespace Flotilla.Mods;

public static class Folders
{
    public static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var folder in Directory.EnumerateDirectories(from)) Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    public static void Replace(string folder, string with)
    {
        var newer = folder + ".newer";
        if (Directory.Exists(newer)) Directory.Delete(newer, recursive: true);
        if (Directory.Exists(folder)) Directory.Move(folder, newer);
        try
        {
            if (SameDrive(folder, with)) Directory.Move(with, folder);
            else Copy(with, folder);
        }
        catch
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            if (Directory.Exists(newer)) Directory.Move(newer, folder);
            throw;
        }
        if (Directory.Exists(newer)) Directory.Delete(newer, recursive: true);
    }

    static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
