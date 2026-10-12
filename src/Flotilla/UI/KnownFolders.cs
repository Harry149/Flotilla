using System.IO;
using System.Runtime.InteropServices;

namespace Flotilla.UI;

public static class KnownFolders
{
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads
    {
        get
        {
            var id = DownloadsId;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
