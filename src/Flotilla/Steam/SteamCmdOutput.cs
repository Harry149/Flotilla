using System.Text.RegularExpressions;

namespace Flotilla.Steam;

public enum ItemState { Downloading, Done, Failed }

public sealed record SteamCmdEvent(ulong Id, ItemState State, string? Reason = null, double Progress = 0);

public static class SteamCmdOutput
{
    static readonly Regex Downloading = new(@"Downloading item (\d+)", RegexOptions.Compiled);
    static readonly Regex Downloaded = new(@"Success\. Downloaded item (\d+)", RegexOptions.Compiled);
    static readonly Regex Failed = new(@"ERROR! Download item (\d+) failed \(([^)]*)\)", RegexOptions.Compiled);
    static readonly Regex TimedOut = new(@"ERROR! Timeout downloading item (\d+)", RegexOptions.Compiled);
    static readonly Regex DepotFailed = new(@"Depot download failed : (?:.*\(([^)]*)\)|(.*))", RegexOptions.Compiled);

    public const string AnonymousRefused = "Steam wouldn't allow an anonymous download, so this mod may need an account that owns UBOAT";
    public const string VersionGone = "Steam no longer has that version";

    public static SteamCmdEvent? ReadDepot(string line)
    {
        if (line.Contains("Depot download complete")) return new(0, ItemState.Done);
        if (DepotFailed.Match(line) is { Success: true } failed)
        {
            var reason = failed.Groups[1].Success ? failed.Groups[1].Value : failed.Groups[2].Value.Trim();
            return new(0, ItemState.Failed, reason == "Manifest unavailable" ? VersionGone : Explain(reason));
        }
        if (line.Contains("ERROR! Not logged on")) return new(0, ItemState.Failed, AnonymousRefused);
        return null;
    }

    public static SteamCmdEvent? Read(string line)
    {
        if (Downloaded.Match(line) is { Success: true } done) return new(Id(done), ItemState.Done);
        if (Failed.Match(line) is { Success: true } failed) return new(Id(failed), ItemState.Failed, Explain(failed.Groups[2].Value));
        if (TimedOut.Match(line) is { Success: true } slow) return new(Id(slow), ItemState.Failed, "the download timed out");
        if (Downloading.Match(line) is { Success: true } started) return new(Id(started), ItemState.Downloading);
        if (line.Contains("ERROR! Not logged on")) return new(0, ItemState.Failed, AnonymousRefused);
        return null;
    }

    static ulong Id(Match match) => ulong.Parse(match.Groups[1].Value);

    static string Explain(string reason) => reason switch
    {
        "Failure" => "Steam reported a failure",
        "Timeout" => "the download timed out",
        "Access Denied" => AnonymousRefused,
        "File Not Found" => "the mod is no longer on the Workshop",
        _ => reason.ToLowerInvariant(),
    };
}
