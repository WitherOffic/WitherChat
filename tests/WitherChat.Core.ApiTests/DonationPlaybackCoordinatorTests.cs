using System.Collections.Concurrent;
using System.Net.WebSockets;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class DonationPlaybackCoordinatorTests
{
    [Fact]
    public async Task BurstOfOneHundredDonationsIsDeduplicatedAndPlayedInFifoOrder()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donations = Enumerable.Range(1, 100).Select(CreateDonation).ToArray();
        var played = new List<string>();
        var maxPlaying = 0;
        coordinator.StateChanged += (_, snapshot) =>
        {
            if (snapshot.State == DonationPlaybackState.Playing && snapshot.CurrentDonation is not null)
            {
                played.Add(snapshot.CurrentDonation.Id);
                maxPlaying = Math.Max(maxPlaying, 1);
            }
        };

        foreach (var donation in donations)
        {
            Assert.True(coordinator.TryEnqueueLive(donation));
            Assert.False(coordinator.TryEnqueueLive(donation));
        }

        await WaitForAsync(
            () => coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart &&
                  coordinator.Snapshot.PendingCount == 99);
        foreach (var donation in donations)
        {
            controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
            await WaitForAsync(() =>
                coordinator.Snapshot.State == DonationPlaybackState.Playing &&
                coordinator.Snapshot.CurrentDonation?.Id == donation.Id);
            Assert.True(coordinator.Snapshot.CanHide);
            controller.Raise(DonationAlertsPlaybackAction.Ended, donation.Id);
            await WaitForAsync(() => coordinator.Snapshot.CurrentDonation?.Id != donation.Id);
        }

        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
        Assert.Equal(donations.Select(donation => donation.Id), played);
        Assert.Equal(1, maxPlaying);
        Assert.Equal(0, controller.RepeatCalls);
    }

    [Fact]
    public async Task LiveDedupeHistoryIsBoundedWithoutAllowingDuplicatesStillInTheQueue()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);

        for (var id = 1; id <= 10_100; id++)
        {
            Assert.True(coordinator.TryEnqueueLive(CreateDonation(id)));
        }

        Assert.Equal(10_000, coordinator.RememberedLiveDonationCount);
        Assert.False(coordinator.TryEnqueueLive(CreateDonation(1)));
        Assert.False(coordinator.TryEnqueueLive(CreateDonation(50)));
        Assert.False(coordinator.TryEnqueueReplay(CreateDonation(1)));
    }

    [Fact]
    public async Task TwoHundredReplayAndHideClicksProduceOneCommandEach()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(500);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        for (var index = 1; index < 200; index++)
        {
            Assert.False(coordinator.TryEnqueueReplay(donation));
        }
        await WaitForAsync(() => controller.RepeatCalls == 1);
        Assert.False(coordinator.Snapshot.CanHide);
        Assert.False(await coordinator.HideCurrentAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, controller.SkipCalls);

        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        var hideTasks = Enumerable.Range(0, 200)
            .Select(_ => coordinator.HideCurrentAsync(TestContext.Current.CancellationToken))
            .ToArray();
        await WaitForAsync(() => controller.SkipCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.Skipped, donation.Id);
        await Task.WhenAll(hideTasks);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);

        Assert.Equal(1, controller.RepeatCalls);
        Assert.Equal(1, controller.SkipCalls);
        Assert.False(coordinator.Snapshot.CanHide);
    }

    [Fact]
    public async Task CancelingTheCallerWaitDoesNotOpenASecondHideCommandBeforeServerConfirmation()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(501);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);

        using var callerCancellation = new CancellationTokenSource();
        var firstHide = coordinator.HideCurrentAsync(callerCancellation.Token);
        await WaitForAsync(() => controller.SkipCalls == 1);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstHide);

        var repeatedClicks = Enumerable.Range(0, 200)
            .Select(_ => coordinator.HideCurrentAsync(TestContext.Current.CancellationToken))
            .ToArray();
        Assert.All(await Task.WhenAll(repeatedClicks), Assert.False);
        Assert.Equal(1, controller.SkipCalls);

        controller.Raise(DonationAlertsPlaybackAction.Skipped, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
        Assert.Equal(1, controller.SkipCalls);
    }

    [Fact]
    public async Task SkipStartsNextDonationAndOutOfOrderTerminalDoesNotStallQueue()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var first = CreateDonation(1);
        var second = CreateDonation(2);
        var third = CreateDonation(3);
        Assert.True(coordinator.TryEnqueueLive(first));
        Assert.True(coordinator.TryEnqueueLive(second));
        Assert.True(coordinator.TryEnqueueLive(third));

        controller.Raise(DonationAlertsPlaybackAction.Ended, third.Id);
        controller.Raise(DonationAlertsPlaybackAction.Started, first.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        controller.Raise(DonationAlertsPlaybackAction.Skipped, first.Id);
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart &&
            coordinator.Snapshot.PendingCount == 0);

        controller.Raise(DonationAlertsPlaybackAction.Started, second.Id);
        await WaitForAsync(() => coordinator.Snapshot.CurrentDonation?.Id == second.Id);
        controller.Raise(DonationAlertsPlaybackAction.Ended, second.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
    }

    [Fact]
    public async Task ConnectionLossStopsReplayWithoutAutomaticDuplicate()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(900);
        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);

        controller.FailPendingReplay(new IOException("socket lost"));
        controller.Raise(DonationAlertsPlaybackAction.ConnectionLost, "0");
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Error);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, controller.RepeatCalls);
        Assert.False(coordinator.Snapshot.CanHide);
        Assert.NotEmpty(coordinator.Snapshot.Error);
    }

    [Fact]
    public async Task ConnectionRestoreClearsErrorWithoutAutomaticallyReplayingDonation()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(902);
        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);

        controller.FailPendingReplay(new IOException("socket lost"));
        controller.Raise(DonationAlertsPlaybackAction.ConnectionLost, "0");
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Error);

        controller.Raise(DonationAlertsPlaybackAction.ConnectionRestored, "0");
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, controller.RepeatCalls);
        Assert.Empty(coordinator.Snapshot.Error);
        Assert.False(coordinator.Snapshot.CanHide);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 2);
    }

    [Fact]
    public async Task NewLiveDonationStartsNormallyAfterConnectionIsRestored()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        controller.Raise(DonationAlertsPlaybackAction.ConnectionLost, "0");
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Error);
        controller.Raise(DonationAlertsPlaybackAction.ConnectionRestored, "0");
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);

        var donation = CreateDonation(905);
        Assert.True(coordinator.TryEnqueueLive(donation));
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.Playing &&
            coordinator.Snapshot.CurrentDonation?.Id == donation.Id);
    }

    [Fact]
    public async Task MissingLiveServerStartTimesOutAndAdvancesTheFifoQueue()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(
            controller,
            () => true,
            TimeSpan.FromMilliseconds(60));
        var first = CreateDonation(906);
        var second = CreateDonation(907);
        Assert.True(coordinator.TryEnqueueLive(first));
        Assert.True(coordinator.TryEnqueueLive(second));

        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart &&
            coordinator.Snapshot.PendingCount == 0);
        controller.Raise(DonationAlertsPlaybackAction.Started, second.Id);
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.Playing &&
            coordinator.Snapshot.CurrentDonation?.Id == second.Id);

        Assert.Equal(0, controller.RepeatCalls);
    }

    [Fact]
    public async Task ConnectionLossReleasesStalePendingQueueBeforeExplicitReplayAfterRestore()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var active = CreateDonation(903);
        var stalePending = CreateDonation(904);

        Assert.True(coordinator.TryEnqueueReplay(active));
        Assert.True(coordinator.TryEnqueueReplay(stalePending));
        await WaitForAsync(() =>
            controller.RepeatCalls == 1 &&
            coordinator.Snapshot.PendingCount == 1);

        controller.Raise(DonationAlertsPlaybackAction.ConnectionLost, "0");
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.Error &&
            coordinator.Snapshot.PendingCount == 0);

        controller.Raise(DonationAlertsPlaybackAction.ConnectionRestored, "0");
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
        Assert.Equal(1, controller.RepeatCalls);

        Assert.True(coordinator.TryEnqueueReplay(stalePending));
        await WaitForAsync(() =>
            controller.RepeatCalls == 2 &&
            coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart);
        Assert.Equal(0, coordinator.Snapshot.PendingCount);
    }

    [Fact]
    public async Task LateTerminalCannotCancelReplayWaitingForItsServerStart()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var first = CreateDonation(910);
        var second = CreateDonation(911);

        Assert.True(coordinator.TryEnqueueReplay(first));
        Assert.True(coordinator.TryEnqueueReplay(second));
        await WaitForAsync(() =>
            controller.RepeatCalls == 1 &&
            coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart);

        controller.Raise(DonationAlertsPlaybackAction.Ended, first.Id);
        controller.Raise(DonationAlertsPlaybackAction.Skipped, second.Id);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(DonationPlaybackState.WaitingForServerStart, coordinator.Snapshot.State);
        Assert.Equal(1, coordinator.Snapshot.PendingCount);
        Assert.Equal(1, controller.RepeatCalls);

        controller.Raise(DonationAlertsPlaybackAction.Started, first.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        controller.Raise(DonationAlertsPlaybackAction.Skipped, first.Id);
        await WaitForAsync(() => controller.RepeatCalls == 2);

        Assert.Equal(DonationPlaybackState.WaitingForServerStart, coordinator.Snapshot.State);
        controller.Raise(DonationAlertsPlaybackAction.Started, second.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        Assert.Equal(second.Id, coordinator.Snapshot.CurrentDonation?.Id);
    }

    [Fact]
    public async Task RepeatedAlternatingReplayAndSkipIgnoresStaleTerminalForSameDonationId()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donations = new[] { CreateDonation(920), CreateDonation(921) };

        for (var cycle = 0; cycle < 20; cycle++)
        {
            var donation = donations[cycle % donations.Length];
            Assert.True(coordinator.TryEnqueueReplay(donation));
            await WaitForAsync(() =>
                controller.RepeatCalls == cycle + 1 &&
                coordinator.Snapshot.State == DonationPlaybackState.WaitingForServerStart);

            controller.Raise(DonationAlertsPlaybackAction.Skipped, donation.Id);
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Equal(DonationPlaybackState.WaitingForServerStart, coordinator.Snapshot.State);

            controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
            await WaitForAsync(() =>
                coordinator.Snapshot.State == DonationPlaybackState.Playing &&
                coordinator.Snapshot.CurrentDonation?.Id == donation.Id);
            Assert.True(coordinator.Snapshot.CanHide);

            controller.Raise(DonationAlertsPlaybackAction.Skipped, donation.Id);
            await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
            Assert.False(coordinator.Snapshot.CanHide);
        }

        Assert.Equal(20, controller.RepeatCalls);
    }

    [Fact]
    public async Task FailedReplayCanOnlyBeRetriedByAnExplicitNewClick()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(901);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);
        controller.FailPendingReplay(new TimeoutException("server start timeout"));
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Error);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, controller.RepeatCalls);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 2);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);

        Assert.Equal(2, controller.RepeatCalls);
        Assert.True(coordinator.Snapshot.CanHide);
    }

    [Fact]
    public async Task ReplayFailureWhileAnotherServerAlertPlaysCannotRequeueADeadOperation()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var replay = CreateDonation(908);
        var live = CreateDonation(909);
        Assert.True(coordinator.TryEnqueueReplay(replay));
        Assert.True(coordinator.TryEnqueueLive(live));
        await WaitForAsync(() => controller.RepeatCalls == 1);

        controller.Raise(DonationAlertsPlaybackAction.Started, live.Id);
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.Playing &&
            coordinator.Snapshot.CurrentDonation?.Id == live.Id);
        controller.FailPendingReplay(new TimeoutException("replay failed"));
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.False(coordinator.TryEnqueueReplay(live));
        controller.Raise(DonationAlertsPlaybackAction.Ended, live.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);
        Assert.True(coordinator.TryEnqueueReplay(replay));
        await WaitForAsync(() => controller.RepeatCalls == 2);
    }

    [Fact]
    public async Task LateEventsAfterIdleCannotConsumeNextExplicitReplayOfSameDonation()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(930);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        controller.Raise(DonationAlertsPlaybackAction.Ended, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);

        // These packets belong to the already completed playback and arrive late.
        controller.Raise(DonationAlertsPlaybackAction.Ended, donation.Id);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 2);
        Assert.Equal(DonationPlaybackState.WaitingForServerStart, coordinator.Snapshot.State);
        Assert.Null(coordinator.Snapshot.CurrentDonation);
        Assert.False(coordinator.Snapshot.CanHide);

        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        Assert.Equal(donation.Id, coordinator.Snapshot.CurrentDonation?.Id);
    }

    [Fact]
    public async Task WebSocketFailureDuringReplayCannotLeaveCoordinatorWaitingForever()
    {
        var controller = new RecordingPlaybackController
        {
            RepeatException = new WebSocketException("socket send failed")
        };
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(940);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Error);

        Assert.Equal(1, controller.RepeatCalls);
        Assert.Null(coordinator.Snapshot.CurrentDonation);
        Assert.False(coordinator.Snapshot.CanHide);
        Assert.Contains("socket", coordinator.Snapshot.Error, StringComparison.OrdinalIgnoreCase);

        controller.RepeatException = null;
        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 2);
    }

    [Fact]
    public async Task WebSocketFailureDuringHideRestoresHideForConfirmedActiveAlert()
    {
        var controller = new RecordingPlaybackController
        {
            SkipException = new WebSocketException("socket skip failed")
        };
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(941);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);

        Assert.False(await coordinator.HideCurrentAsync(TestContext.Current.CancellationToken));
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.Playing &&
            coordinator.Snapshot.CanHide &&
            coordinator.Snapshot.Error.Length > 0);

        Assert.Equal(1, controller.SkipCalls);
        Assert.Equal(donation.Id, coordinator.Snapshot.CurrentDonation?.Id);
    }

    [Fact]
    public async Task NaturalEndRacingHideRequestCannotRestoreACompletedPlayingSnapshot()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(942);
        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);

        controller.Raise(DonationAlertsPlaybackAction.Ended, donation.Id);
        var hidden = await coordinator.HideCurrentAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);

        Assert.False(hidden);
        Assert.Equal(0, controller.SkipCalls);
        Assert.Null(coordinator.Snapshot.CurrentDonation);
        Assert.False(coordinator.Snapshot.CanHide);
    }

    [Fact]
    public async Task UnknownServerSignalBufferIsBoundedAndClearedOnConnectionLoss()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);

        for (var index = 1; index <= 5_000; index++)
        {
            controller.Raise(DonationAlertsPlaybackAction.Started, index.ToString());
            controller.Raise(DonationAlertsPlaybackAction.Ended, index.ToString());
        }
        controller.Raise(DonationAlertsPlaybackAction.ConnectionLost, "0");
        await WaitForAsync(() =>
            coordinator.Snapshot.State == DonationPlaybackState.Error &&
            coordinator.BufferedServerSignalCount == 0);
        Assert.InRange(coordinator.MaximumObservedBufferedServerSignalCount, 1, 512);
        Assert.Equal(DonationPlaybackState.Error, coordinator.Snapshot.State);
    }

    [Fact]
    public async Task CompletedLiveDonationIdRemainsDeduplicatedButExplicitReplayIsAllowed()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(controller, () => true);
        var donation = CreateDonation(8801);

        Assert.True(coordinator.TryEnqueueLive(donation));
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
        controller.Raise(DonationAlertsPlaybackAction.Ended, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Idle);

        Assert.False(coordinator.TryEnqueueLive(donation));
        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => controller.RepeatCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.Started, donation.Id);
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Playing);
    }

    [Fact]
    public async Task ReplayWaitingForServerIsCanceledAtTimeoutAndCannotResumeOnReconnect()
    {
        var controller = new RecordingPlaybackController();
        await using var coordinator = new DonationPlaybackCoordinator(
            controller,
            () => true,
            TimeSpan.FromMilliseconds(60));
        var donation = CreateDonation(8802);

        Assert.True(coordinator.TryEnqueueReplay(donation));
        await WaitForAsync(() => coordinator.Snapshot.State == DonationPlaybackState.Error);
        await WaitForAsync(() => controller.CanceledRepeatCalls == 1);
        controller.Raise(DonationAlertsPlaybackAction.ConnectionRestored, "0");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, controller.RepeatCalls);
        Assert.Equal(1, controller.CanceledRepeatCalls);
        Assert.Null(coordinator.Snapshot.CurrentDonation);
        Assert.False(coordinator.Snapshot.CanHide);
    }

    private static DonationAlert CreateDonation(int id) => new(
        id.ToString(),
        "Viewer " + id,
        "Message " + id,
        id,
        "RUB",
        DateTimeOffset.UtcNow.AddMilliseconds(id));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected playback state was not reached.");
            }
            await Task.Delay(5);
        }
    }

    private sealed class RecordingPlaybackController : IDonationAlertsObsController
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _starts = new();
        private TaskCompletionSource? _skip;
        private int _repeatCalls;
        private int _skipCalls;
        private int _canceledRepeatCalls;

        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;
        public bool IsConfigured => true;
        public bool IsConnected => true;
        public bool IsAlertPlaying { get; private set; }
        public string ActiveAlertId { get; private set; } = string.Empty;
        public string SourceName => string.Empty;
        public int RepeatCalls => Volatile.Read(ref _repeatCalls);
        public int SkipCalls => Volatile.Read(ref _skipCalls);
        public int CanceledRepeatCalls => Volatile.Read(ref _canceledRepeatCalls);
        public Exception? RepeatException { get; set; }
        public Exception? SkipException { get; set; }

        public bool TryAutoConfigure() => true;
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task RepeatDonationAsync(
            DonationAlert donation,
            CancellationToken cancellationToken = default)
        {
            if (RepeatException is not null)
            {
                Interlocked.Increment(ref _repeatCalls);
                throw RepeatException;
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _starts[donation.Id] = completion;
            Interlocked.Increment(ref _repeatCalls);
            try
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceledRepeatCalls);
                throw;
            }
        }

        public Task SkipDonationAsync(
            DonationAlert donation,
            CancellationToken cancellationToken = default)
        {
            if (SkipException is not null)
            {
                Interlocked.Increment(ref _skipCalls);
                return Task.FromException(SkipException);
            }
            _skip ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _skipCalls);
            return _skip.Task.WaitAsync(cancellationToken);
        }

        public void Raise(DonationAlertsPlaybackAction action, string donationId)
        {
            var id = long.Parse(donationId);
            if (action == DonationAlertsPlaybackAction.Started)
            {
                IsAlertPlaying = true;
                ActiveAlertId = donationId;
                if (_starts.TryGetValue(donationId, out var start))
                {
                    start.TrySetResult();
                }
            }
            else
            {
                IsAlertPlaying = false;
                ActiveAlertId = string.Empty;
                if (action == DonationAlertsPlaybackAction.Skipped)
                {
                    _skip?.TrySetResult();
                }
            }
            PlaybackChanged?.Invoke(this, new DonationAlertsPlaybackEventArgs(action, id));
        }

        public void FailPendingReplay(Exception exception)
        {
            foreach (var pair in _starts.ToArray())
            {
                pair.Value.TrySetException(exception);
                _starts.TryRemove(pair.Key, out _);
            }
        }
    }
}
