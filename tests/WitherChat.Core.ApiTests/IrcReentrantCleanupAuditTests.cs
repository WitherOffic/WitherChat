using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class IrcReentrantCleanupAuditTests
{
    [Fact]
    public async Task ReentrantDisposalFromStatusCallbackUsesTheSameCleanup()
    {
        await using var client = new TwitchIrcClient();
        Task? reentered = null;
        var notifications = 0;
        client.StatusChanged += (_, _) =>
        {
            Interlocked.Increment(ref notifications);
            reentered ??= client.DisposeAsync().AsTask();
        };
        var original = client.DisposeAsync().AsTask();
        await original.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.NotNull(reentered);
        await reentered.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Same(original, reentered);
        Assert.Equal(1, notifications);
    }
}
