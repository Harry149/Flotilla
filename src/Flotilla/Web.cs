using System.Net.Http;

namespace Flotilla;

public static class Web
{
    public static HttpClient Client { get; } = Create();

    static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Flotilla/{Updater.Current}");
        return client;
    }
}
