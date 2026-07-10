using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class EmoteCache
{
    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxImageDimension = 1024;
    private const int MaxAnimationFrames = 300;
    private const int MaxStaticImages = 256;
    private const int MaxAnimatedMedia = 512;
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "static-cdn.jtvnw.net",
        "cdn.betterttv.net",
        "cdn.7tv.app"
    };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly FileLogger _logger;
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<EmoteMedia?>> _media = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _downloads = new(StringComparer.Ordinal);

    public EmoteCache(FileLogger logger)
    {
        _logger = logger;
    }

    public Task<ImageSource?> GetImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return Task.FromResult<ImageSource?>(null);
        }

        var task = _images.GetOrAdd(imageUrl, LoadImageAsync);
        TrimCompletedEntries(_images, MaxStaticImages);
        return task.WaitAsync(cancellationToken);
    }

    public Task<EmoteMedia?> GetMediaAsync(
        string cacheKey,
        string imageUrl,
        string? fallbackImageUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(imageUrl))
        {
            return Task.FromResult<EmoteMedia?>(null);
        }

        var task = _media.GetOrAdd(cacheKey, _ => LoadMediaWithFallbackAsync(imageUrl, fallbackImageUrl));
        TrimCompletedEntries(_media, MaxAnimatedMedia);
        return task.WaitAsync(cancellationToken);
    }

    private async Task<ImageSource?> LoadImageAsync(string imageUrl)
    {
        var bytes = await GetBytesAsync(imageUrl).ConfigureAwait(false);
        return bytes is null ? null : DecodeMedia(bytes)?.FirstFrame;
    }

    private async Task<EmoteMedia?> LoadMediaWithFallbackAsync(string imageUrl, string? fallbackImageUrl)
    {
        var bytes = await GetBytesAsync(imageUrl).ConfigureAwait(false);
        var media = bytes is null ? null : DecodeMedia(bytes);
        if (media is not null || string.IsNullOrWhiteSpace(fallbackImageUrl))
        {
            return media;
        }

        var fallbackBytes = await GetBytesAsync(fallbackImageUrl).ConfigureAwait(false);
        return fallbackBytes is null ? null : DecodeMedia(fallbackBytes);
    }

    private async Task<byte[]?> GetBytesAsync(string imageUrl)
    {
        var task = _downloads.GetOrAdd(imageUrl, DownloadAsync);
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            if (_downloads.TryGetValue(imageUrl, out var current) && ReferenceEquals(current, task))
            {
                _downloads.TryRemove(imageUrl, out _);
            }
        }
    }

    private async Task<byte[]?> DownloadAsync(string imageUrl)
    {
        try
        {
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !AllowedImageHosts.Contains(uri.Host))
            {
                _logger.Warn("Blocked untrusted emote image URL.");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"Emote image failed: {(int)response.StatusCode} {SanitizeUrl(imageUrl)}");
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                _logger.Warn($"Emote image exceeds size limit: {SanitizeUrl(imageUrl)}");
                return null;
            }

            await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaxImageBytes)
                {
                    _logger.Warn($"Emote image exceeds size limit: {SanitizeUrl(imageUrl)}");
                    return null;
                }

                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }

            return output.ToArray();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Emote image load failed: {ex.GetType().Name} {SanitizeUrl(imageUrl)}");
            return null;
        }
    }

    private static EmoteMedia? DecodeMedia(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0 || decoder.Frames.Count > MaxAnimationFrames ||
                decoder.Frames[0].PixelWidth > MaxImageDimension || decoder.Frames[0].PixelHeight > MaxImageDimension)
            {
                return null;
            }

            var frames = decoder.Frames.Cast<BitmapFrame>().Select(frame =>
            {
                if (frame.CanFreeze)
                {
                    frame.Freeze();
                }

                return (ImageSource)frame;
            }).ToArray();
            if (frames.Length == 0)
            {
                return null;
            }

            var delays = decoder.Frames.Select(GetFrameDelay).ToArray();
            return new EmoteMedia(frames, delays);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan GetFrameDelay(BitmapFrame frame)
    {
        if (frame.Metadata is BitmapMetadata metadata)
        {
            try
            {
                if (metadata.GetQuery("/grctlext/Delay") is ushort gifDelay)
                {
                    return TimeSpan.FromMilliseconds(Math.Max(20, gifDelay * 10));
                }
            }
            catch
            {
            }
        }

        return TimeSpan.FromMilliseconds(100);
    }

    private static string SanitizeUrl(string url)
    {
        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        return queryIndex >= 0 ? url[..queryIndex] : url;
    }

    private static void TrimCompletedEntries<T>(ConcurrentDictionary<string, Task<T>> cache, int maxCount)
    {
        if (cache.Count <= maxCount)
        {
            return;
        }

        foreach (var entry in cache)
        {
            if (cache.Count <= maxCount * 3 / 4)
            {
                break;
            }

            if (entry.Value.IsCompleted)
            {
                cache.TryRemove(entry.Key, out _);
            }
        }
    }
}
