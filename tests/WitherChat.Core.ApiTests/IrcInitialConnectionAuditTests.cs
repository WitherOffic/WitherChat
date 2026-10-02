using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class IrcInitialConnectionAuditTests
{
    [Fact]
    public async Task FailedInitialConnectionClearsMembershipSoUiCanRetry()
    {
        var attempts = 0;
        await using var client = new TwitchIrcClient(async (_, initial, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("Simulated initial connection failure.");
            initial.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await Assert.ThrowsAsync<IOException>(() =>
            client.ConnectAsync("alpha", TestContext.Current.CancellationToken));
        Assert.Empty(client.Channels);
        Assert.Empty(client.CurrentChannel);
        await client.JoinChannelAsync("alpha", TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { "alpha" }, client.Channels);
    }

    [Fact]
    public async Task CancellingInitialConnectionClearsAttemptedMembership()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new TwitchIrcClient(async (_, initial, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connect = client.ConnectAsync("alpha", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.Empty(client.Channels);
        Assert.Empty(client.CurrentChannel);
        Assert.False(client.IsConnected);
    }
}
