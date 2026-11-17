using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace MioReel.Desktop.Controls;

/// <summary>
/// 轻量封面图异步下载与解码缓存（限定缓存规模，避免虚拟化滚动时重复拉图或内存膨胀）。
/// </summary>
internal static class CoverImageCache
{
    private const int MaxCacheEntries = 200;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new(StringComparer.Ordinal);

    public static bool TryGetCached(string? url, out Bitmap? bitmap)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            bitmap = null;
            return false;
        }

        if (Cache.TryGetValue(url, out var hit))
        {
            bitmap = hit;
            return true;
        }

        bitmap = null;
        return false;
    }

    public static Task<Bitmap?> LoadAsync(string url)
    {
        if (Cache.TryGetValue(url, out var hit))
            return Task.FromResult<Bitmap?>(hit);

        return InFlight.GetOrAdd(url, FetchCoreAsync);
    }

    private static async Task<Bitmap?> FetchCoreAsync(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            using var resp = await Http.GetAsync(uri);
            if (!resp.IsSuccessStatusCode)
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0)
                return null;

            var bmp = await Task.Run(() =>
            {
                using var ms = new MemoryStream(bytes, writable: false);
                return Bitmap.DecodeToWidth(ms, 360);
            });

            if (Cache.Count >= MaxCacheEntries)
            {
                foreach (var oldKey in Cache.Keys.Take(40))
                    Cache.TryRemove(oldKey, out _);
            }

            Cache[url] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }
}
