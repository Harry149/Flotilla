namespace Flotilla.Steam;

public sealed record ChangeNote(DateTime Time, string Text);

public sealed record WorkshopItem(
    ulong Id,
    string Title,
    string Description,
    string PreviewUrl,
    long Size,
    DateTime Created,
    DateTime Updated,
    int Subscribers,
    string[] Tags,
    ulong Manifest = 0,
    ulong Creator = 0);
