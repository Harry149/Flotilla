namespace Flotilla.Steam;

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
    ulong Manifest = 0);
