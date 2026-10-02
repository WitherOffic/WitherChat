using System.Reflection;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class IrcHandshakeAuditTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MembershipChangesDuringRestoreMatchTheServerAfterHandshake(bool adding)
    {
        await using var client = new TwitchIrcClient();
        Set(client, "_runTask", Task.CompletedTask);
        Set(client, "_currentChannel", "alpha");
        var desired = (HashSet<string>)Get(client, "_channels")!;
        desired.Add("alpha");
        if (!adding) desired.Add("beta");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<string>();
        var server = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restore = client.RestoreChannelsAsync( async (line, token) =>
        {
            commands.Add(line);
            if (commands.Count == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            var channel = line.Split('#')[1];
            if (line.StartsWith("JOIN", StringComparison.Ordinal)) server.Add(channel);
            else server.Remove(channel);
        }, TestContext.Current.CancellationToken);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (adding) await client.JoinChannelAsync("beta", TestContext.Current.CancellationToken);
            else await client.PartChannelAsync("alpha", TestContext.Current.CancellationToken);
        }
        finally { release.TrySetResult(); }
        await restore.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(client.Channels.Order(), server.Order());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task EmptyDesiredMembershipDoesNotRejoinTheRemovedFallbackChannel()
    {
        await using var client = new TwitchIrcClient();
        var commands = new List<string>();
        await client.RestoreChannelsAsync( (line, token) =>
        {
            commands.Add(line);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task CancelledRestoreCannotMarkTheTransportReady()
    {
        await using var client = new TwitchIrcClient();
        ((HashSet<string>)Get(client, "_channels")!).Add("alpha");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RestoreChannelsAsync( (line, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.False(client.IsConnected);
    }

    private static object? Get(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
