using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class EmoteCache : IAsyncDisposable
{
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxImageDimension = 512;
    private const int MaxAnimationFrames = 180;
    private const long MaxDecodedPixelsPerMedia = 12L * 1024 * 1024;
    private const long SoftDecodedBytes = 224L * 1024 * 1024;
    private const long HardDecodedBytes = 384L * 1024 * 1024;
    private const int SoftDecodedEntries = 800;
    private const int HardDecodedEntries = 1200;
    private static readonly TimeSpan TrimInterval = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "static-cdn.jtvnw.net",
        "cdn.betterttv.net",
        "cdn.7tv.app"
    };
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly FileLogger _logger;
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<EmoteMedia?>> _media = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _downloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastAccess = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _decodedSizes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _operations = new();
    private readonly object _trimGate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private long _nextTrimUtcTicks;
    private long _approximateDecodedBytes;
    private int _disposed;
    private readonly SemaphoreSlim _decodeGate = new(3, 3);
    private readonly SemaphoreSlim _downloadGate = new(10, 10);

    public EmoteCache(FileLogger logger)
    {
        _logger = logger;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();

        while (!_operations.IsEmpty)
        {
            var tasks = _operations.Keys.ToArray();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.Warn($"Emote cache shutdown observed a failed operation: {ex.GetBaseException().GetType().Name}");
            }
        }

        _http.Dispose();
        _decodeGate.Dispose();
        _downloadGate.Dispose();
        _lifetimeCts.Dispose();
    }

    internal int StaticImageCount => _images.Count;
    internal int MediaCount => _media.Count;
    internal int PendingDownloadCount => _downloads.Count;
    internal long ApproximateDecodedBytes => CalculateDecodedBytes();

    public async Task<ImageSource?> GetImageAsync(string imageUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var task = _images.GetOrAdd(
            imageUrl,
            CreateImageLoadTask);
        Touch("image:" + imageUrl);
        try
        {
            var image = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (image is BitmapSource bitmap)
            {
                AccountDecodedBytes("image:" + imageUrl, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
            }

            return image;
        }
        finally
        {
            RemoveImageTaskIfUnusable(imageUrl, task);
            TrimDecodedCacheIfNeeded();
        }
    }

    internal bool TryGetCachedImage(string imageUrl, out ImageSource? image)
    {
        image = null;
        if (string.IsNullOrWhiteSpace(imageUrl) ||
            !_images.TryGetValue(imageUrl, out var task) ||
            !task.IsCompletedSuccessfully)
        {
            return false;
        }

        image = task.Result;
        if (image is null)
        {
            return false;
        }

        Touch("image:" + imageUrl);
        return true;
    }

    internal bool TryGetImageTask(string imageUrl, out Task<ImageSource?> imageTask)
    {
        if (!string.IsNullOrWhiteSpace(imageUrl) &&
            _images.TryGetValue(imageUrl, out var existingTask))
        {
            Touch("image:" + imageUrl);
            imageTask = existingTask;
            return true;
        }

        imageTask = null!;
        return false;
    }

    internal bool TryGetCachedMedia(string cacheKey, out EmoteMedia? media)
    {
        media = null;
        if (string.IsNullOrWhiteSpace(cacheKey) ||
            !_media.TryGetValue(cacheKey, out var task) ||
            !task.IsCompletedSuccessfully)
        {
            return false;
        }

        media = task.Result;
        if (media is null)
        {
            return false;
        }

        Touch("media:" + cacheKey);
        return true;
    }

    public async Task<EmoteMedia?> GetMediaAsync(
        string cacheKey,
        string imageUrl,
        string? fallbackImageUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        var task = _media.GetOrAdd(
            cacheKey,
            _ => CreateMediaLoadTask(cacheKey, imageUrl, fallbackImageUrl));
        Touch("media:" + cacheKey);
        try
        {
            var media = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (media is not null)
            {
                AccountDecodedBytes("media:" + cacheKey, media.DecodedPixelCount * 4);
            }

            return media;
        }
        finally
        {
            RemoveMediaTaskIfUnusable(cacheKey, task);
            TrimDecodedCacheIfNeeded();
        }
    }

    private async Task<ImageSource?> LoadImageAsync(string imageUrl, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : (await DecodeMediaAsync(bytes, cancellationToken).ConfigureAwait(false))?.FirstFrame;
    }

    private async Task<EmoteMedia?> LoadMediaWithFallbackAsync(
        string imageUrl,
        string? fallbackImageUrl,
        CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        var media = bytes is null ? null : await DecodeMediaAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (media is not null || string.IsNullOrWhiteSpace(fallbackImageUrl))
        {
            return media;
        }

        var fallbackBytes = await GetBytesAsync(fallbackImageUrl, cancellationToken).ConfigureAwait(false);
        return fallbackBytes is null ? null : await DecodeMediaAsync(fallbackBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EmoteMedia?> DecodeMediaAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return DecodeMedia(bytes);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private async Task<byte[]?> GetBytesAsync(string imageUrl, CancellationToken cancellationToken)
    {
        var task = _downloads.GetOrAdd(
            imageUrl,
            CreateDownloadTask);
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                RemoveDownloadTask(imageUrl, task);
            }
        }
    }

    private Task<ImageSource?> CreateImageLoadTask(string imageUrl)
    {
        var task = TrackOperation(LoadImageAsync(imageUrl, _lifetimeCts.Token));
        _ = task.ContinueWith(
            completed => RemoveImageTaskIfUnusable(imageUrl, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private Task<EmoteMedia?> CreateMediaLoadTask(string cacheKey, string imageUrl, string? fallbackImageUrl)
    {
        var task = TrackOperation(LoadMediaWithFallbackAsync(imageUrl, fallbackImageUrl, _lifetimeCts.Token));
        _ = task.ContinueWith(
            completed => RemoveMediaTaskIfUnusable(cacheKey, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private Task<byte[]?> CreateDownloadTask(string imageUrl)
    {
        var task = TrackOperation(DownloadAsync(imageUrl, _lifetimeCts.Token));
        _ = task.ContinueWith(
            completed => RemoveDownloadTask(imageUrl, completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private void RemoveImageTaskIfUnusable(string imageUrl, Task<ImageSource?> task)
    {
        if (!IsUnusable(task) ||
            !_images.TryGetValue(imageUrl, out var current) ||
            !ReferenceEquals(current, task))
        {
            return;
        }

        _images.TryRemove(imageUrl, out _);
        RemoveAccountedBytes("image:" + imageUrl);
    }

    private void RemoveMediaTaskIfUnusable(string cacheKey, Task<EmoteMedia?> task)
    {
        if (!IsUnusable(task) ||
            !_media.TryGetValue(cacheKey, out var current) ||
            !ReferenceEquals(current, task))
        {
            return;
        }

        _media.TryRemove(cacheKey, out _);
        RemoveAccountedBytes("media:" + cacheKey);
    }

    private void RemoveDownloadTask(string imageUrl, Task<byte[]?> task)
    {
        if (_downloads.TryGetValue(imageUrl, out var current) && ReferenceEquals(current, task))
        {
            _downloads.TryRemove(imageUrl, out _);
        }
    }

    private static bool IsUnusable<T>(Task<T?> task) where T : class =>
        task.IsCanceled ||
        task.IsFaulted ||
        task.Status == TaskStatus.RanToCompletion && task.Result is null;

    private async Task<byte[]?> DownloadAsync(string imageUrl, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _downloadGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(imageUrl, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private async Task<byte[]?> DownloadCoreAsync(string imageUrl, CancellationToken cancellationToken)
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
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
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

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > MaxImageBytes)
                {
                    _logger.Warn($"Emote image exceeds size limit: {SanitizeUrl(imageUrl)}");
                    return null;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            using (var probeStream = new MemoryStream(bytes, writable: false))
            {
                var probe = BitmapDecoder.Create(probeStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (!TryMeasureFrames(probe.Frames, out _))
                {
                    return null;
                }
            }

            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (IsGif(bytes) && decoder.Frames.Count > 1)
            {
                return ComposeGifMedia(decoder);
            }
            if (!TryMeasureFrames(decoder.Frames, out var decodedPixels))
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
            return new EmoteMedia(frames, delays) { DecodedPixelCount = decodedPixels };
        }
        catch
        {
            return null;
        }
    }

    private static EmoteMedia? ComposeGifMedia(BitmapDecoder decoder)
    {
        var canvasWidth = GetMetadataInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width");
        var canvasHeight = GetMetadataInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height");
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            canvasWidth = decoder.Frames.Max(frame =>
                GetMetadataInt(frame.Metadata as BitmapMetadata, "/imgdesc/Left") + frame.PixelWidth);
            canvasHeight = decoder.Frames.Max(frame =>
                GetMetadataInt(frame.Metadata as BitmapMetadata, "/imgdesc/Top") + frame.PixelHeight);
        }

        var decodedPixels = (long)canvasWidth * canvasHeight * decoder.Frames.Count;
        if (canvasWidth <= 0 || canvasHeight <= 0 ||
            canvasWidth > MaxImageDimension || canvasHeight > MaxImageDimension ||
            decoder.Frames.Count > MaxAnimationFrames || decodedPixels > MaxDecodedPixelsPerMedia)
        {
            return null;
        }

        var stride = checked(canvasWidth * 4);
        var canvas = new byte[checked(stride * canvasHeight)];
        byte[]? restorePrevious = null;
        var previousDisposal = 0;
        var previousRect = default(FrameRectangle);
        var outputFrames = new List<ImageSource>(decoder.Frames.Count);
        var delays = new List<TimeSpan>(decoder.Frames.Count);

        foreach (var frame in decoder.Frames)
        {
            if (previousDisposal == 2)
            {
                ClearRectangle(canvas, stride, canvasWidth, canvasHeight, previousRect);
            }
            else if (previousDisposal == 3 && restorePrevious is not null)
            {
                Buffer.BlockCopy(restorePrevious, 0, canvas, 0, canvas.Length);
            }

            var metadata = frame.Metadata as BitmapMetadata;
            var left = Math.Max(0, GetMetadataInt(metadata, "/imgdesc/Left"));
            var top = Math.Max(0, GetMetadataInt(metadata, "/imgdesc/Top"));
            var rect = new FrameRectangle(left, top, frame.PixelWidth, frame.PixelHeight);
            var disposal = GetMetadataInt(metadata, "/grctlext/Disposal");
            var restoreForCurrent = disposal == 3 ? (byte[])canvas.Clone() : null;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            var sourceStride = checked(frame.PixelWidth * 4);
            var source = new byte[checked(sourceStride * frame.PixelHeight)];
            converted.CopyPixels(source, sourceStride, 0);
            BlendFrame(canvas, stride, canvasWidth, canvasHeight, source, sourceStride, rect);

            var composed = BitmapSource.Create(
                canvasWidth,
                canvasHeight,
                frame.DpiX > 0 ? frame.DpiX : 96,
                frame.DpiY > 0 ? frame.DpiY : 96,
                PixelFormats.Bgra32,
                null,
                (byte[])canvas.Clone(),
                stride);
            composed.Freeze();
            outputFrames.Add(composed);
            delays.Add(GetFrameDelay(frame));

            previousDisposal = disposal;
            previousRect = rect;
            restorePrevious = restoreForCurrent;
        }

        return new EmoteMedia(outputFrames, delays) { DecodedPixelCount = decodedPixels };
    }

    private static void BlendFrame(
        byte[] canvas,
        int canvasStride,
        int canvasWidth,
        int canvasHeight,
        byte[] source,
        int sourceStride,
        FrameRectangle rect)
    {
        var copyWidth = Math.Min(rect.Width, canvasWidth - rect.Left);
        var copyHeight = Math.Min(rect.Height, canvasHeight - rect.Top);
        if (copyWidth <= 0 || copyHeight <= 0)
        {
            return;
        }

        for (var y = 0; y < copyHeight; y++)
        {
            for (var x = 0; x < copyWidth; x++)
            {
                var sourceOffset = y * sourceStride + x * 4;
                var targetOffset = (rect.Top + y) * canvasStride + (rect.Left + x) * 4;
                var sourceAlpha = source[sourceOffset + 3];
                if (sourceAlpha == 0)
                {
                    continue;
                }
                if (sourceAlpha == 255)
                {
                    Buffer.BlockCopy(source, sourceOffset, canvas, targetOffset, 4);
                    continue;
                }

                var destinationAlpha = canvas[targetOffset + 3];
                var inverseSourceAlpha = 255 - sourceAlpha;
                var outputAlpha = sourceAlpha + (destinationAlpha * inverseSourceAlpha + 127) / 255;
                for (var channel = 0; channel < 3; channel++)
                {
                    var numerator = source[sourceOffset + channel] * sourceAlpha * 255 +
                                    canvas[targetOffset + channel] * destinationAlpha * inverseSourceAlpha;
                    canvas[targetOffset + channel] = outputAlpha == 0
                        ? (byte)0
                        : (byte)Math.Clamp((numerator + outputAlpha * 127) / (outputAlpha * 255), 0, 255);
                }
                canvas[targetOffset + 3] = (byte)outputAlpha;
            }
        }
    }

    private static void ClearRectangle(
        byte[] canvas,
        int stride,
        int canvasWidth,
        int canvasHeight,
        FrameRectangle rect)
    {
        var width = Math.Min(rect.Width, canvasWidth - rect.Left);
        var height = Math.Min(rect.Height, canvasHeight - rect.Top);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            Array.Clear(canvas, (rect.Top + y) * stride + rect.Left * 4, width * 4);
        }
    }

    private static int GetMetadataInt(BitmapMetadata? metadata, string query)
    {
        try
        {
            return metadata?.GetQuery(query) is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsGif(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 6 &&
        bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
        bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') && bytes[5] == (byte)'a';

    private readonly record struct FrameRectangle(int Left, int Top, int Width, int Height);

    private static bool TryMeasureFrames(IReadOnlyList<BitmapFrame> frames, out long decodedPixels)
    {
        decodedPixels = 0;
        if (frames.Count == 0 || frames.Count > MaxAnimationFrames)
        {
            return false;
        }

        foreach (var frame in frames)
        {
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                frame.PixelWidth > MaxImageDimension || frame.PixelHeight > MaxImageDimension)
            {
                return false;
            }

            decodedPixels += (long)frame.PixelWidth * frame.PixelHeight;
            if (decodedPixels > MaxDecodedPixelsPerMedia)
            {
                return false;
            }
        }

        return true;
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

    private void Touch(string key) => _lastAccess[key] = DateTimeOffset.UtcNow.UtcTicks;

    private Task<T> TrackOperation<T>(Task<T> task)
    {
        _operations.TryAdd(task, 0);
        _ = task.ContinueWith(
            completed => _operations.TryRemove(completed, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private void AccountDecodedBytes(string key, long bytes)
    {
        if (_decodedSizes.TryAdd(key, Math.Max(0, bytes)))
        {
            Interlocked.Add(ref _approximateDecodedBytes, Math.Max(0, bytes));
        }
    }

    private void RemoveAccountedBytes(string key)
    {
        if (_decodedSizes.TryRemove(key, out var bytes))
        {
            Interlocked.Add(ref _approximateDecodedBytes, -bytes);
        }
    }

    private void TrimDecodedCacheIfNeeded()
    {
        var entryCount = _images.Count + _media.Count;
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        var hardPressure = entryCount > HardDecodedEntries ||
                           Volatile.Read(ref _approximateDecodedBytes) > HardDecodedBytes;
        if (!hardPressure && nowTicks < Volatile.Read(ref _nextTrimUtcTicks))
        {
            return;
        }

        lock (_trimGate)
        {
            entryCount = _images.Count + _media.Count;
            var decodedBytes = CalculateDecodedBytes();
            hardPressure = entryCount > HardDecodedEntries || decodedBytes > HardDecodedBytes;
            if (!hardPressure && entryCount <= SoftDecodedEntries && decodedBytes <= SoftDecodedBytes)
            {
                Volatile.Write(ref _nextTrimUtcTicks, DateTimeOffset.UtcNow.Add(TrimInterval).UtcTicks);
                return;
            }

            var targetEntries = hardPressure ? SoftDecodedEntries : SoftDecodedEntries * 9 / 10;
            var targetBytes = hardPressure ? SoftDecodedBytes : SoftDecodedBytes * 9 / 10;
            var candidates = GetEvictionCandidates()
                .OrderBy(candidate => candidate.LastAccessTicks)
                .ToArray();
            foreach (var candidate in candidates)
            {
                if (entryCount <= targetEntries && decodedBytes <= targetBytes)
                {
                    break;
                }

                var removed = candidate.IsMedia
                    ? _media.TryRemove(candidate.CacheKey, out _)
                    : _images.TryRemove(candidate.CacheKey, out _);
                if (!removed)
                {
                    continue;
                }

                _lastAccess.TryRemove(candidate.TouchKey, out _);
                RemoveAccountedBytes(candidate.TouchKey);
                entryCount--;
                decodedBytes = Math.Max(0, decodedBytes - candidate.DecodedBytes);
            }

            Volatile.Write(ref _nextTrimUtcTicks, DateTimeOffset.UtcNow.Add(TrimInterval).UtcTicks);
        }
    }

    private IEnumerable<CacheCandidate> GetEvictionCandidates()
    {
        foreach (var entry in _images)
        {
            if (!entry.Value.IsCompleted)
            {
                continue;
            }

            var touchKey = "image:" + entry.Key;
            var bytes = _decodedSizes.GetValueOrDefault(touchKey);
            yield return new CacheCandidate(
                entry.Key,
                touchKey,
                IsMedia: false,
                _lastAccess.GetValueOrDefault(touchKey),
                bytes);
        }

        foreach (var entry in _media)
        {
            if (!entry.Value.IsCompleted)
            {
                continue;
            }

            var touchKey = "media:" + entry.Key;
            var bytes = _decodedSizes.GetValueOrDefault(touchKey);
            yield return new CacheCandidate(
                entry.Key,
                touchKey,
                IsMedia: true,
                _lastAccess.GetValueOrDefault(touchKey),
                bytes);
        }
    }

    private long CalculateDecodedBytes() => Math.Max(0, Volatile.Read(ref _approximateDecodedBytes));

    private sealed record CacheCandidate(
        string CacheKey,
        string TouchKey,
        bool IsMedia,
        long LastAccessTicks,
        long DecodedBytes);
}
