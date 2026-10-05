using System.Net;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class EventSubDisposalRaceAuditTests
{
    private static TwitchAuthSession Session => new("synthetic-token", "", TwitchApplication.ClientId,
        "owner", "owner", TwitchApplication.RequiredScopes, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);

    [Fact]
    public async Task ConfigurationQueuedBeforeDisposalCannotRunAfterShutdownBegins()
    {
        using var handler = new DelayedLookup();
        using var api = new TwitchChatApiClient(handler: handler);
        await using var service = new TwitchEventSubClient(api);
        var first = service.ConfigureAsync(Session, ["first"], TestContext.Current.CancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var queued = service.ConfigureAsync(Session, ["second"], TestContext.Current.CancellationToken);
        var disposing = service.DisposeAsync().AsTask();
        handler.Release.TrySetResult();
        await first;
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AllDisposalCallersWaitForTheInFlightConfiguration()
    {
        using var handler = new DelayedLookup();
        using var api = new TwitchChatApiClient(handler: handler);
        await using var service = new TwitchEventSubClient(api);
        var configure = service.ConfigureAsync(Session, ["first"], TestContext.Current.CancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var first = service.DisposeAsync().AsTask();
        var second = service.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            handler.Release.TrySetResult();
            await configure;
            await Task.WhenAll(first, second);
        }
    }

    private sealed class DelayedLookup : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{}") };
        }
    }
}
