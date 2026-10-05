using System.Net;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class QueueCancellationR11Tests
{
    [AvaloniaFact]
    public async Task ClosingCacheCancelsQueuedPublicationsWithoutNeedingAnotherUiTick()
    {
        var handler = new ImageHandler();
        using var cache = new ChatImageCache(handler);
        var uris = Enumerable.Range(0, 16)
            .Select(i => new Uri($"https://static-cdn.jtvnw.net/r11/cancel-{i}.gif")).ToArray();
        var preload = cache.PreloadAsync(uris, TestContext.Current.CancellationToken);
        Assert.True(SpinWait.SpinUntil(() => handler.Requests >= 8, 5000));
        // Downloads and tiny decodes have finished; intentionally do not pump
        // the UI dispatcher until after disposal has completed on workers.
        Thread.Sleep(200);
        cache.Dispose();
        Assert.True(SpinWait.SpinUntil(() => preload.IsCompleted, 2000),
            "Queued UI publications must cancel even when the UI loop has stopped.");
        await preload;
        Assert.False(cache.IsLoading);
        Assert.Equal(0, cache.CachedDecodedBytes);
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Convert.FromBase64String(
                    "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
                    "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7"))
            });
        }
    }
}
