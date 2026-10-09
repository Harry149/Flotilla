namespace Flotilla.UI;

public sealed class StatusLine : Observable
{
    int shown;

    public bool IsWorking { get; private set => Set(ref field, value); }
    public string WorkTitle { get; private set => Set(ref field, value); } = "";
    public string WorkDetail { get; private set => Set(ref field, value); } = "";
    public double Progress { get; private set => Set(ref field, value); }
    public ModTile? WorkTile { get; private set => Set(ref field, value); }

    public string Message { get; private set => Set(ref field, value); } = "";
    public bool IsError { get; private set => Set(ref field, value); }
    public bool HasMessage { get; private set => Set(ref field, value); }

    public void Work(string title, string detail, double progress = 0, ModTile? tile = null)
    {
        WorkTitle = title;
        WorkDetail = detail;
        Progress = Math.Clamp(progress, 0, 1);
        WorkTile = tile;
        IsWorking = true;
    }

    public void Idle()
    {
        IsWorking = false;
        Progress = 0;
        WorkTile = null;
    }

    public void Say(string text) => _ = ShowAsync(text, error: false, TimeSpan.FromSeconds(5));

    public void Warn(string text) => _ = ShowAsync(text, error: true, TimeSpan.FromSeconds(12));

    public void Dismiss()
    {
        shown++;
        HasMessage = false;
    }

    async Task ShowAsync(string text, bool error, TimeSpan duration)
    {
        var mine = ++shown;
        Message = text;
        IsError = error;
        HasMessage = true;

        await Task.Delay(duration);
        if (mine == shown) HasMessage = false;
    }
}
