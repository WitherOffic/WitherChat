
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;

public sealed class PlaybackShutdownAuditTests
{
    [Fact]
    public async Task ClosedPlaybackCannotReconnectTheExternalController()
    {
        var controller = new Controller();
        var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        await coordinator.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.EnsureConnectedAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, controller.ConnectCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingPlaybackDrainsAllWaitersAndSettlesQueuedHide(bool queuedHide)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var coordinator = new DonationPlaybackCoordinator(new Controller(), () => true);
        coordinator.StateChanged += (_, state) =>
        {
            if (state.State != DonationPlaybackState.WaitingForServerStart) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) throw new TimeoutException("Test did not release consumer");
        };
        Assert.True(coordinator.TryEnqueueLive(
            new DonationAlert("1", "Viewer", "Synthetic", 1, "RUB", DateTimeOffset.UtcNow)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        var hide = queuedHide ? coordinator.HideCurrentAsync(caller.Token) : null;
        var first = coordinator.DisposeAsync().AsTask();
        var second = coordinator.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            if(!queuedHide) Assert.False(second.IsCompleted);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, second);
        }
        try
        {
            if(hide is not null)
            {
                var settled = await Task.WhenAny(hide, Task.Delay(500, TestContext.Current.CancellationToken));
                Assert.Same(hide, settled);
                Assert.False(await hide);
            }
        }
        finally
        {
            await caller.CancelAsync();
            if(hide is not null) {try {await hide;} catch(OperationCanceledException) {}}
            await coordinator.DisposeAsync();
        }
    }

    private sealed class Controller : IDonationAlertsObsController
    {
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged { add { } remove { } }
        public int ConnectCalls;
        public bool IsConfigured => true;
        public bool IsConnected => true;
        public bool IsAlertPlaying => false;
        public string ActiveAlertId => "";
        public string SourceName => "";
        public bool TryAutoConfigure() => true;
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {Interlocked.Increment(ref ConnectCalls);return Task.CompletedTask;}
        public Task RepeatDonationAsync(DonationAlert donation,CancellationToken cancellationToken = default)=>Task.CompletedTask;
        public Task SkipDonationAsync(DonationAlert donation,CancellationToken cancellationToken = default)=>Task.CompletedTask;
    }
}
