using System.IO;
using System.Text.RegularExpressions;

namespace Flotilla.Steam;

sealed class LogTail(string path)
{
    static readonly Regex Escape = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    long offset = File.Exists(path) ? new FileInfo(path).Length : 0;
    string partial = "";

    public List<string> ReadNew()
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset)
            {
                offset = 0;
                partial = "";
            }
            if (stream.Length == offset) return lines;

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = partial + reader.ReadToEnd();
            offset = stream.Position;

            var parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            partial = parts[^1];
            lines.AddRange(parts[..^1].Select(part => Escape.Replace(part, "").Trim()).Where(line => line.Length > 0));
        }
        catch (IOException)
        {
        }
        return lines;
    }
}
