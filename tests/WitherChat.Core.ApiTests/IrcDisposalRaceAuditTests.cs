using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class IrcDisposalRaceAuditTests
{
    [Theory]
    [InlineData("connect")]
    [InlineData("join")]
    [InlineData("part")]
    public async Task QueuedMembershipWorkCannotRunAfterShutdownBegins(string operation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var client = new TwitchIrcClient(async (_, ready, cancellation) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            ready.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
        });
        var first = client.ConnectAsync("first", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Task queued = operation switch
        {
            "connect" => client.ConnectAsync("second", TestContext.Current.CancellationToken),
            "join" => client.JoinChannelAsync("second", TestContext.Current.CancellationToken),
            _ => client.PartChannelAsync("first", TestContext.Current.CancellationToken)
        };
        var disposing = client.DisposeAsync().AsTask();
        release.TrySetResult();
        await first;
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.Equal(1, calls);
        Assert.Empty(client.Channels);
    }

    [Fact]
    public async Task ConcurrentDisposalWaitsForTheFirstCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new TwitchIrcClient(async (_, ready, cancellation) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            ready.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
        });
        var configure = client.ConnectAsync("first", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var first = client.DisposeAsync().AsTask();
        var second = client.DisposeAsync().AsTask();
        try { Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); }
        finally
        {
            release.TrySetResult();
            await configure;
            await Task.WhenAll(first, second);
        }
    }
}
