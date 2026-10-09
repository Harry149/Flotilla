using System.ComponentModel;
using System.Diagnostics;

namespace Flotilla.UI;

public static class Shell
{
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static void OpenInSteam(ulong id)
    {
        if (!Open($"steam://url/CommunityFilePage/{id}"))
            Open($"https://steamcommunity.com/sharedfiles/filedetails/?id={id}");
    }
}
