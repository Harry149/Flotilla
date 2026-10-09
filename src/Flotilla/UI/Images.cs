using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Flotilla.UI;

public static class Images
{
    static readonly string Cache = Directory.CreateDirectory(Path.Combine(Paths.AppData, "images")).FullName;
    static readonly Dictionary<string, Task<BitmapSource?>> Pending = [];

    public static string Sized(string url, int width) =>
        url.Length == 0 ? url : $"{url}{(url.Contains('?') ? '&' : '?')}imw={width}&ima=fit&impolicy=Letterbox&letterbox=false";

    public static Task<BitmapSource?> Load(string url, int width)
    {
        var key = $"{width} {url}";
        lock (Pending)
        {
            if (Pending.TryGetValue(key, out var running)) return running;

            var task = Task.Run(() => Fetch(url, width));
            Pending[key] = task;
            task.ContinueWith(_ => { lock (Pending) Pending.Remove(key); }, TaskScheduler.Default);
            return task;
        }
    }

    static async Task<BitmapSource?> Fetch(string url, int width)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        var file = Path.Combine(Cache, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))));
        try
        {
            if (!File.Exists(file))
            {
                var bytes = await Web.Client.GetByteArrayAsync(uri);
                await File.WriteAllBytesAsync(file + ".part", bytes);
                File.Move(file + ".part", file, overwrite: true);
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = width;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class LazyImage(string url, int width, Action changed)
{
    WeakReference<BitmapSource>? image;
    bool loading, failed;

    public BitmapSource? Value
    {
        get
        {
            if (image is not null && image.TryGetTarget(out var ready)) return ready;
            if (!loading && !failed && url.Length > 0) _ = LoadAsync();
            return null;
        }
    }

    async Task LoadAsync()
    {
        loading = true;
        var loaded = await Images.Load(url, width);
        loading = false;
        failed = loaded is null;
        if (loaded is null) return;

        image = new WeakReference<BitmapSource>(loaded);
        changed();
    }
}
