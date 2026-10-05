using System.Reflection;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class DonationAnimationGenerationAuditTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OldTransitionCannotChangeANewPlaybackOfTheSameDonation(bool entering)
    {
        await using var coordinator = new DonationPlaybackCoordinator(new Controller(), () => false);
        var type = typeof(DonationPlaybackCoordinator);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var donation = new DonationAlert("1", "Viewer", "Text", 1, "RUB", DateTimeOffset.UtcNow);
        var entryType = type.GetNestedType("QueueEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, donation, 1L, true, 2L)!;
        type.GetMethod("BeginEntering", flags)!.Invoke(coordinator, [entry]);
        var transitionType = type.GetNestedType("TransitionFinished", BindingFlags.NonPublic)!;
        var stale = Activator.CreateInstance(transitionType, 1L, entering, DonationPlaybackState.Completed)!;
        type.GetMethod("ProcessTransitionFinished", flags)!.Invoke(coordinator, [stale]);
        Assert.Equal(DonationPlaybackState.Entering, coordinator.Snapshot.State);
        Assert.Equal("1", coordinator.Snapshot.CurrentDonation!.Id);
        Assert.False(coordinator.Snapshot.CanHide);
    }
    private sealed class Controller : IDonationAlertsObsController
    {
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged { add { } remove { } }
        public bool IsConfigured => true;
        public bool IsConnected => true;
        public bool IsAlertPlaying => false;
        public string ActiveAlertId => "";
        public string SourceName => "";
        public bool TryAutoConfigure() => true;
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RepeatDonationAsync(DonationAlert donation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipDonationAsync(DonationAlert donation, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
