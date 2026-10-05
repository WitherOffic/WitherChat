using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class MemoryBudgetR10Tests
{
    private const string Gif =
        "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
        "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7";

    [Fact]
    public void WindowsStandaloneAndDockUseTheSameNonEglRenderingPath()
    {
        var options = Program.CreateWindowsPlatformOptions();
        Assert.Equal([Win32RenderingMode.Software], options.RenderingMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCacheByteBudgetIsRejected(long budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChatImageCache(null, TimeSpan.FromSeconds(1), budget));
    }

    [AvaloniaFact]
    public void AllAnimationFramesCountTowardNativePixelsAndDisposalIsIdempotent()
    {
        var media = ChatImageDecoder.Decode(Convert.FromBase64String(Gif));
        Assert.NotNull(media);
        Assert.Equal(8, media.DecodedByteCount); // 2 frames * 1 * 1 * 4, not encoded length.
        media.Dispose();
        Assert.Null(Record.Exception(media.Dispose));
    }

    [AvaloniaFact]
    public async Task ByteBudgetEvictsLruBeforeEntryCountLimitAndKeepsVisibleImageValid()
    {
        using var cache = Cache(16);
        var first = UriFor(1);
        var second = UriFor(2);
        var third = UriFor(3);
        await cache.PreloadAsync([first, second], TestContext.Current.CancellationToken);
        var retained = cache.GetResource(first)!; // Make first most recently used.
        var media = retained.Media;
        await cache.PreloadAsync([third], TestContext.Current.CancellationToken);

        Assert.Equal(16, cache.CachedDecodedBytes);
        Assert.Equal(2, Entries(cache).Count);
        Assert.Contains(first.AbsoluteUri, Entries(cache).Keys);
        Assert.DoesNotContain(second.AbsoluteUri, Entries(cache).Keys);
        Assert.True(cache.GetResource(third)!.HasImage);

        // Evict the first resource while a message still holds it.
        await cache.PreloadAsync([UriFor(4)], TestContext.Current.CancellationToken);
        Assert.DoesNotContain(first.AbsoluteUri, Entries(cache).Keys);
        Assert.Same(media, retained.Media);
        using var encoded = new MemoryStream();
        retained.Image!.Save(encoded, global::Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Assert.True(encoded.Length > 0);
        Assert.InRange(cache.CachedDecodedBytes, 0, 16);
    }

    [AvaloniaFact]
    public async Task ConcurrentPublicationsNeverOvershootAndBudgetFailureCanRetry()
    {
        using var cache = Cache(8);
        var uris = Enumerable.Range(0, 20).Select(UriFor).ToArray();
        foreach (var uri in uris)
        {
            var resource = cache.GetResource(uri)!;
            resource.PropertyChanged += (_, _) =>
                Assert.InRange(cache.CachedDecodedBytes, 0, 8);
        }
        await cache.PreloadAsync(uris, TestContext.Current.CancellationToken);
        Assert.InRange(cache.CachedDecodedBytes, 0, 8);

        // Completed entries are now safe eviction victims; a rejected image is
        // not permanently stuck displaying its fallback.
        var retryUri = uris.FirstOrDefault(uri => !Entries(cache).TryGetValue(uri.AbsoluteUri,
            out var resource) || !resource.HasImage) ?? UriFor(21);
        await cache.PreloadAsync([retryUri], TestContext.Current.CancellationToken);
        Assert.True(cache.GetResource(retryUri)!.HasImage);
        Assert.Equal(8, cache.CachedDecodedBytes);
        Assert.False(cache.IsLoading);
    }

    [AvaloniaFact]
    public async Task RealDefaultBudgetBoundsLargeNativeFramesAndReloadsEvictedImages()
    {
        using var pixels = new SkiaSharp.SKBitmap(512, 512);
        pixels.Erase(SkiaSharp.SKColors.Magenta);
        using var image = SkiaSharp.SKImage.FromBitmap(pixels);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var handler = new GifHandler(encoded.ToArray());
        using var cache = new ChatImageCache(handler);
        for (var index = 0; index < 96; index++)
        {
            await cache.PreloadAsync([UriFor(index)], TestContext.Current.CancellationToken);
            Assert.InRange(cache.CachedDecodedBytes, 0, ChatImageCache.MaximumDecodedBytes);
        }
        Assert.Equal(64L * 1024 * 1024, cache.CachedDecodedBytes);
        Assert.Equal(64, Entries(cache).Count);
        Assert.DoesNotContain(UriFor(0).AbsoluteUri, Entries(cache).Keys);
        await cache.PreloadAsync([UriFor(0)], TestContext.Current.CancellationToken);
        Assert.True(cache.GetResource(UriFor(0))!.HasImage);
        Assert.Equal(64L * 1024 * 1024, cache.CachedDecodedBytes);
        cache.Dispose();
        Assert.Equal(0, cache.CachedDecodedBytes);
    }

    [AvaloniaFact]
    public void NativeMemoryPressureIsBalancedOnceEvenWhenDisposalIsRepeated()
    {
        var media = ChatImageDecoder.Decode(Convert.FromBase64String(Gif))!;
        var field = typeof(ChatImageMedia).GetField("_memoryPressureBytes",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(8L, (long)field.GetValue(media)!);
        media.Dispose();
        media.Dispose();
        Assert.Equal(0L, (long)field.GetValue(media)!);
    }

    [AvaloniaFact]
    public async Task BusyUiCannotAccumulateAnUnboundedDecodedPublicationQueue()
    {
        var handler = new GifHandler();
        using var cache = new ChatImageCache(handler);
        var uris = Enumerable.Range(0, 24).Select(UriFor).ToArray();
        foreach (var uri in uris) cache.GetResource(uri);

        // Keep the UI thread occupied while downloads and decode workers run.
        Assert.True(SpinWait.SpinUntil(() => handler.RequestCount >= 8, 5000));
        Thread.Sleep(200);
        Assert.Equal(8, handler.RequestCount);
        await cache.PreloadAsync(uris, TestContext.Current.CancellationToken);
        Assert.Equal(24, handler.RequestCount);
        Assert.All(uris, uri => Assert.True(cache.GetResource(uri)!.HasImage));
        Assert.False(cache.IsLoading);
    }

    [AvaloniaFact]
    public async Task SingleOversizedMediaIsRejectedWithoutExceedingByteBudget()
    {
        var handler = new GifHandler();
        using var cache = new ChatImageCache(handler, TimeSpan.FromSeconds(2), 4);
        var uri = UriFor(1);
        await cache.PreloadAsync([uri], TestContext.Current.CancellationToken);
        var resource = cache.GetResource(uri)!;
        Assert.False(resource.HasImage);
        Assert.Equal(0, cache.CachedDecodedBytes);
        await cache.PreloadAsync([uri], TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.RequestCount);
    }

    [AvaloniaFact]
    public async Task ClosingFromMediaObserverClearsAccountingAndCannotDoubleDisposeFrames()
    {
        using var cache = Cache(16);
        var uri = UriFor(1);
        var resource = cache.GetResource(uri)!;
        resource.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatImageResource.Media)) cache.Dispose();
        };
        await cache.PreloadAsync([uri], TestContext.Current.CancellationToken);
        Assert.Empty(Entries(cache));
        Assert.Equal(0, cache.CachedDecodedBytes);
        Assert.False(cache.IsLoading);
        Assert.Null(cache.GetResource(uri));
    }

    private static ChatImageCache Cache(long budget) =>
        new(new GifHandler(), TimeSpan.FromSeconds(2), budget);

    private static Uri UriFor(int index) => new($"https://static-cdn.jtvnw.net/r10/{index}.gif");

    private static ConcurrentDictionary<string, ChatImageResource> Entries(ChatImageCache cache) =>
        (ConcurrentDictionary<string, ChatImageResource>)typeof(ChatImageCache)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;

    private sealed class GifHandler(byte[]? payload = null) : HttpMessageHandler
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload ?? Convert.FromBase64String(Gif))
            });
        }
    }
}
