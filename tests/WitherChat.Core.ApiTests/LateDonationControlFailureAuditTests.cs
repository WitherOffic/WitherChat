using System.Reflection;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class LateDonationControlFailureAuditTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HideFailureOnlyChangesItsOwnPlayback(bool serverPlaying, bool currentFailure)
    {
        await using var coordinator = new DonationPlaybackCoordinator(new Controller(serverPlaying), () => false);
        var type = typeof(DonationPlaybackCoordinator);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var donation = new DonationAlert("1", "Viewer", "Text", 1, "RUB", DateTimeOffset.UtcNow);
        var entryType = type.GetNestedType("QueueEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, donation, 1L, true, 2L)!;
        type.GetMethod("BeginEntering", flags)!.Invoke(coordinator, [entry]);
        var before = coordinator.Snapshot;
        var failureType = type.GetNestedType("ControlFailed", BindingFlags.NonPublic)!;
        var constructor = failureType.GetConstructors().Single(c => c.GetParameters().Length == 2);
        object identity = constructor.GetParameters()[0].ParameterType == typeof(string)
            ? "1" : currentFailure ? 2L : 1L;
        var failure = constructor.Invoke([identity, "delayed old skip failure"]);
        type.GetMethod("ProcessControlFailed", flags)!.Invoke(coordinator, [failure]);
        if (!currentFailure)
        {
            Assert.Equal(before, coordinator.Snapshot);
        }
        else
        {
            Assert.Equal(serverPlaying ? DonationPlaybackState.Playing : DonationPlaybackState.Error,
                coordinator.Snapshot.State);
            Assert.Equal("delayed old skip failure", coordinator.Snapshot.Error);
            Assert.Equal(serverPlaying, coordinator.Snapshot.CanHide);
        }
    }
    private sealed class Controller(bool playing) : IDonationAlertsObsController
    {
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged { add { } remove { } }
        public bool IsConfigured => true;
        public bool IsConnected => true;
        public bool IsAlertPlaying => playing;
        public string ActiveAlertId => "1";
        public string SourceName => "";
        public bool TryAutoConfigure() => true;
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RepeatDonationAsync(DonationAlert donation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipDonationAsync(DonationAlert donation, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
