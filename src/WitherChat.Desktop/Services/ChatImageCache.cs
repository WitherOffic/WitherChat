using System.Collections.Concurrent;
using System.Net;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WitherChat.Desktop.Services;

public sealed partial class ChatImageResource : ObservableObject
{
    internal ChatImageResource(double fallbackWidth)
    {
        _displayWidth = fallbackWidth;
    }

    public bool HasImage => Media?.FirstFrame is not null;
    public bool ShowFallback => !HasImage;
    public Bitmap? Image => Media?.FirstFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(ShowFallback))]
    [NotifyPropertyChangedFor(nameof(Image))]
    private ChatImageMedia? _media;

    public double CompactDisplayWidth => DisplayWidth * 11 / 14;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactDisplayWidth))]
    private double _displayWidth;

    internal void EnsureFallbackWidth(double width)
    {
        if (!HasImage && width > DisplayWidth)
        {
            DisplayWidth = width;
        }
    }

    internal void SetMedia(ChatImageMedia media, double displayHeight)
    {
        var image = media.FirstFrame;
        if (image is not null && image.PixelSize.Height > 0)
        {
            DisplayWidth = Math.Clamp(
                displayHeight * image.PixelSize.Width / image.PixelSize.Height,
                8,
                196);
        }

        Media = media;
    }
}

public sealed class ChatImageCache : IDisposable
{
    private const int MaximumEntries = 1_024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const double DisplayHeight = 28;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _downloadTimeout;
    private readonly ConcurrentDictionary<string, ChatImageResource> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _loadTasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _accessOrder = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadingEntries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retryableEntries = new(StringComparer.Ordinal);
    private readonly object _entryGate = new();
    private readonly SemaphoreSlim _downloadGate = new(8, 8);
    private readonly CancellationTokenSource _lifetime = new();
    private int _activeLoadCount;
    private long _accessSequence;
    private bool _disposed;

    public event EventHandler? LoadingStateChanged;

    public bool IsLoading => Volatile.Read(ref _activeLoadCount) > 0;

    public ChatImageCache(HttpMessageHandler? handler = null)
        : this(handler, TimeSpan.FromSeconds(12))
    {
    }

    internal ChatImageCache(HttpMessageHandler? handler, TimeSpan downloadTimeout)
    {
        if (downloadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        }
        _downloadTimeout = downloadTimeout;
        _httpClient = handler is null
            ? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                CheckCertificateRevocationList = true
            })
            : new HttpClient(handler);
        // One deadline covers response headers AND the streamed body. HttpClient's
        // own timeout ends at headers when ResponseHeadersRead is used.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public ChatImageResource? GetResource(Uri imageUri, double fallbackWidth = DisplayHeight)
    {
        ArgumentNullException.ThrowIfNull(imageUri);
        if (_disposed || !IsAllowedImageUri(imageUri))
        {
            return null;
        }

        var imageUrl = imageUri.AbsoluteUri;
        ChatImageResource resource;
        lock (_entryGate)
        {
            if (_entries.TryGetValue(imageUrl, out var existing))
            {
                _accessOrder[imageUrl] = ++_accessSequence;
                existing.EnsureFallbackWidth(fallbackWidth);
                if (_retryableEntries.Remove(imageUrl) && _loadingEntries.Add(imageUrl))
                {
                    BeginLoad();
                    _loadTasks[imageUrl] = Task.Run(() => PopulateAsync(imageUri, existing));
                }
                return existing;
            }

            while (_entries.Count >= MaximumEntries)
            {
                string? evictedUrl = null;
                var oldestAccess = long.MaxValue;
                foreach (var entry in _accessOrder)
                {
                    if (!_loadingEntries.Contains(entry.Key) && entry.Value < oldestAccess)
                    {
                        evictedUrl = entry.Key;
                        oldestAccess = entry.Value;
                    }
                }
                if (evictedUrl is null)
                {
                    return null;
                }

                _entries.TryRemove(evictedUrl, out _);
                _accessOrder.Remove(evictedUrl);
                // Do not dispose here: a currently visible message can still own
                // the resource. Once that UI reference is released, its bitmap is
                // collected normally; the cache itself remains strictly bounded.
            }

            resource = new ChatImageResource(fallbackWidth);
            _entries[imageUrl] = resource;
            _accessOrder[imageUrl] = ++_accessSequence;
            _loadingEntries.Add(imageUrl);
            BeginLoad();
            _loadTasks[imageUrl] = Task.Run(() => PopulateAsync(imageUri, resource));
        }
        return resource;
    }

    public async Task PreloadAsync(
        IEnumerable<Uri?> imageUris,
        CancellationToken cancellationToken = default)
    {
        var urls = imageUris
            .Where(uri => uri is not null && IsAllowedImageUri(uri))
            .Select(uri => uri!.AbsoluteUri)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var url in urls)
        {
            _ = GetResource(new Uri(url));
        }

        var tasks = urls
            .Select(url => _loadTasks.TryGetValue(url, out var task) ? task : Task.CompletedTask)
            .ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        foreach (var resource in _entries.Values)
        {
            resource.Media?.Dispose();
        }

        _entries.Clear();
        _loadTasks.Clear();
        lock (_entryGate)
        {
            _accessOrder.Clear();
            _loadingEntries.Clear();
            _retryableEntries.Clear();
        }
        _httpClient.Dispose();
        _lifetime.Dispose();
    }

    private async Task PopulateAsync(Uri imageUri, ChatImageResource resource)
    {
        var retryable = false;
        try
        {
            var imageUrl = imageUri.AbsoluteUri;
            var (media, isTransientFailure) = await LoadMediaAsync(imageUri).ConfigureAwait(false);
            if (media is null)
            {
                // Cache permanent failures such as 404, but allow a later
                // message to retry temporary CDN/network failures.
                retryable = isTransientFailure;
                return;
            }

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || !_entries.TryGetValue(imageUrl, out var current) || !ReferenceEquals(current, resource))
                    {
                        media.Dispose();
                        return;
                    }

                    resource.SetMedia(media, DisplayHeight);
                });
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or InvalidOperationException && _disposed)
            {
                media.Dispose();
            }
        }
        finally
        {
            var imageUrl = imageUri.AbsoluteUri;
            lock (_entryGate)
            {
                _loadTasks.TryRemove(imageUrl, out _);
                _loadingEntries.Remove(imageUrl);
                if (!_disposed &&
                    _entries.TryGetValue(imageUrl, out var current) &&
                    ReferenceEquals(current, resource))
                {
                    if (retryable)
                    {
                        _retryableEntries.Add(imageUrl);
                    }
                    else
                    {
                        _retryableEntries.Remove(imageUrl);
                    }
                }
            }

            EndLoad();
        }
    }

    private void BeginLoad()
    {
        if (Interlocked.Increment(ref _activeLoadCount) == 1)
        {
            LoadingStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void EndLoad()
    {
        var remaining = Interlocked.Decrement(ref _activeLoadCount);
        if (remaining <= 0)
        {
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _activeLoadCount, 0);
            }

            LoadingStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<(ChatImageMedia? Media, bool IsTransientFailure)> LoadMediaAsync(Uri imageUri)
    {
        var gateTaken = false;
        try
        {
            await _downloadGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            gateTaken = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(_downloadTimeout);
            var downloadToken = timeout.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                downloadToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var transient = response.StatusCode is HttpStatusCode.RequestTimeout or
                    HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                return (null, transient);
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                return (null, false);
            }

            using var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            int read;
            while ((read = await input.ReadAsync(chunk, downloadToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                {
                    return (null, false);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), downloadToken).ConfigureAwait(false);
            }

            return (ChatImageDecoder.Decode(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))), false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return (null, false);
        }
        catch (OperationCanceledException)
        {
            // A CDN timeout is temporary, not a missing image. Retry next time it
            // is requested and never fault the fire-and-forget preload boundary.
            return (null, true);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return (null, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
                                           ArgumentException or NotSupportedException)
        {
            return (null, true);
        }
        finally
        {
            if (gateTaken)
            {
                _downloadGate.Release();
            }
        }
    }

    private static bool IsAllowedImageUri(Uri uri)
    {
        return uri.IsAbsoluteUri &&
               uri.Scheme == Uri.UriSchemeHttps &&
               (uri.Host is "static-cdn.jtvnw.net" or "cdn.betterttv.net" or "cdn.7tv.app" ||
                uri.Host.EndsWith(".ggpht.com", StringComparison.OrdinalIgnoreCase));
    }
}
