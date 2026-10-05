using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ImageEvictionAuditTests
{
    [Fact]
    public async Task TransientFailureRetryStateIsBoundedTogetherWithImageEntries()
    {
        using var cache = new ChatImageCache(new UnavailableImageHandler());
        for (var batch = 0; batch < 12; batch++)
        {
            var uris = Enumerable.Range(batch * 128, 128)
                .Select(index => new Uri($"https://static-cdn.jtvnw.net/audit/{index}.png"));
            await cache.PreloadAsync(uris, TestContext.Current.CancellationToken);
        }
        var entries = Private<ConcurrentDictionary<string, ChatImageResource>>(cache, "_entries");
        var retryable = Private<HashSet<string>>(cache, "_retryableEntries");
        Assert.Equal(1024, entries.Count);
        Assert.InRange(retryable.Count, 1, 1024);
        Assert.All(retryable, url => Assert.Contains(url, entries.Keys));
        Assert.False(cache.IsLoading);
    }

    [Fact]
    public async Task EvictedTransientImageCanBeRequestedAgainAndDisposalClearsRetryState()
    {
        var handler = new UnavailableImageHandler();
        var cache = new ChatImageCache(handler);
        try
        {
            var oldest = new Uri("https://static-cdn.jtvnw.net/audit/oldest.png");
            await cache.PreloadAsync([oldest], TestContext.Current.CancellationToken);
            for (var batch = 0; batch < 8; batch++)
            {
                var uris = Enumerable.Range(batch * 128, 128)
                    .Select(index => new Uri($"https://static-cdn.jtvnw.net/audit/{index}.png"));
                await cache.PreloadAsync(uris, TestContext.Current.CancellationToken);
            }
            var entries = Private<ConcurrentDictionary<string, ChatImageResource>>(cache, "_entries");
            var retryable = Private<HashSet<string>>(cache, "_retryableEntries");
            Assert.False(entries.ContainsKey(oldest.AbsoluteUri));
            Assert.DoesNotContain(oldest.AbsoluteUri, retryable);
            await cache.PreloadAsync([oldest], TestContext.Current.CancellationToken);
            Assert.True(entries.ContainsKey(oldest.AbsoluteUri));
            Assert.Contains(oldest.AbsoluteUri, retryable);
            cache.Dispose();
            Assert.Empty(retryable);
            Assert.Empty(entries);
            Assert.Null(cache.GetResource(oldest));
        }
        finally
        {
            cache.Dispose();
        }
    }

    private static T Private<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance) ?? throw new InvalidOperationException("Missing field: " + name));

    private sealed class UnavailableImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
