using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public enum DonationPlaybackState
{
    Idle,
    Queued,
    WaitingForServerStart,
    Entering,
    Playing,
    Exiting,
    Completed,
    Skipped,
    Error
}

public sealed record DonationPlaybackSnapshot(
    DonationPlaybackState State,
    DonationAlert? CurrentDonation,
    int PendingCount,
    bool CanHide,
    string Error);

public sealed class DonationPlaybackCoordinator : IAsyncDisposable
{
    public const string ConnectionLostError = "DonationAlerts direct control connection was lost.";
    public const string ServerStartTimeoutError = "DonationAlerts did not confirm that the alert started.";
    private const int MaximumBufferedServerSignals = 512;
    private const int MaximumRememberedLiveDonationIds = 10_000;
    private static readonly TimeSpan MotionDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan DefaultServerStartTimeout = TimeSpan.FromSeconds(35);
    private readonly IDonationAlertsObsController _controller;
    private readonly Func<bool> _reduceMotionProvider;
    private readonly TimeSpan _serverStartTimeout;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _admissionGate = new();
    private readonly HashSet<string> _admittedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenLiveIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenLiveIdOrder = new();
    private readonly Queue<QueueEntry> _queue = new();
    private readonly Dictionary<long, long> _earlyStarts = [];
    private readonly Dictionary<long, (DonationAlertsPlaybackAction Action, long Version)> _earlyTerminals = [];
    private readonly Queue<(bool IsStart, long AlertId, long Version)> _earlySignalOrder = new();
    private readonly Task _consumerTask;
    private DonationPlaybackSnapshot _snapshot = new(
        DonationPlaybackState.Idle,
        null,
        0,
        false,
        string.Empty);
    private QueueEntry? _active;
    private int _hideCommandInFlight;
    private long _nextOperationId;
    private long _bufferVersion;
    private int _maximumObservedBufferedServerSignalCount;
    private bool _disposed;
    private bool _halted;

    public DonationPlaybackCoordinator(
        IDonationAlertsObsController controller,
        Func<bool>? reduceMotionProvider = null,
        TimeSpan? serverStartTimeout = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _reduceMotionProvider = reduceMotionProvider ?? (() => false);
        _serverStartTimeout = serverStartTimeout ?? DefaultServerStartTimeout;
        if (_serverStartTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(serverStartTimeout));
        }
        _controller.PlaybackChanged += OnPlaybackChanged;
        _consumerTask = ConsumeAsync(_lifetimeCancellation.Token);
    }

    public event EventHandler<DonationPlaybackSnapshot>? StateChanged;

    public DonationPlaybackSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool TryEnqueueLive(DonationAlert donation) => TryEnqueue(donation, replay: false);

    public bool TryEnqueueReplay(DonationAlert donation) => TryEnqueue(donation, replay: true);

    public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        _controller.EnsureConnectedAsync(cancellationToken);

    public void ClearPending()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _commands.Writer.TryWrite(new ClearPendingQueue());
    }

    public async Task<bool> HideCurrentAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _hideCommandInFlight, 1, 0) != 0)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(new RequestHide(completion)))
        {
            Interlocked.Exchange(ref _hideCommandInFlight, 0);
            return false;
        }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _controller.PlaybackChanged -= OnPlaybackChanged;
        _commands.Writer.TryComplete();
        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _consumerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        ClearBufferedServerSignals();
        _lifetimeCancellation.Dispose();
    }

    private bool TryEnqueue(DonationAlert donation, bool replay)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!long.TryParse(
                donation.Id,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var alertId) ||
            alertId <= 0)
        {
            return false;
        }

        lock (_admissionGate)
        {
            if (!replay &&
                (_seenLiveIds.Contains(donation.Id) || _admittedIds.Contains(donation.Id)))
            {
                return false;
            }
            if (!replay)
            {
                RememberLiveDonationId(donation.Id);
            }
            if (!_admittedIds.Add(donation.Id))
            {
                return false;
            }
        }
        var operationId = Interlocked.Increment(ref _nextOperationId);
        if (_commands.Writer.TryWrite(new Enqueue(new QueueEntry(donation, alertId, replay, operationId))))
        {
            return true;
        }
        ReleaseAdmission(donation.Id);
        if (!replay)
        {
            lock (_admissionGate)
            {
                ForgetLiveDonationId(donation.Id);
            }
        }
        return false;
    }

    private void OnPlaybackChanged(object? sender, DonationAlertsPlaybackEventArgs eventArgs) =>
        _commands.Writer.TryWrite(new ServerPlayback(eventArgs.Action, eventArgs.AlertId));

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (command)
            {
                case Enqueue enqueue:
                    ProcessEnqueue(enqueue.Entry);
                    break;
                case ServerPlayback playback:
                    ProcessServerPlayback(playback);
                    break;
                case ReplayFinished replayFinished:
                    ProcessReplayFinished(replayFinished);
                    break;
                case StartWaitExpired startWaitExpired:
                    ProcessStartWaitExpired(startWaitExpired);
                    break;
                case TransitionFinished transitionFinished:
                    ProcessTransitionFinished(transitionFinished);
                    break;
                case ControlFailed controlFailed:
                    ProcessControlFailed(controlFailed);
                    break;
                case RequestHide requestHide:
                    ProcessRequestHide(requestHide);
                    break;
                case ClearPendingQueue:
                    ClearQueuedEntries();
                    PublishCurrent();
                    break;
            }
        }
    }

    private void ProcessEnqueue(QueueEntry entry)
    {
        if (entry.Replay)
        {
            // A replay is a new, explicit operation for an existing DonationAlerts ID.
            // Delayed events from an earlier playback must never confirm or cancel it.
            _earlyStarts.Remove(entry.AlertId);
            _earlyTerminals.Remove(entry.AlertId);
        }
        else if (_earlyTerminals.Remove(entry.AlertId, out _))
        {
            ReleaseAdmission(entry.Donation.Id);
            PublishCurrent();
            return;
        }

        // A retry is always an explicit user action. It may resume a queue that was
        // halted after a connection/command failure, but nothing is replayed on its own.
        if (entry.Replay && _halted && _active is null)
        {
            _halted = false;
        }

        _queue.Enqueue(entry);
        if (_active is null && !_halted)
        {
            StartNext();
        }
        else
        {
            PublishCurrent();
        }

        if (!entry.Replay && _earlyStarts.Remove(entry.AlertId))
        {
            ProcessServerPlayback(new ServerPlayback(DonationAlertsPlaybackAction.Started, entry.AlertId));
        }
    }

    private void StartNext()
    {
        if (_active is not null || _queue.Count == 0 || _halted)
        {
            PublishCurrent();
            return;
        }

        _active = _queue.Dequeue();
        Publish(new DonationPlaybackSnapshot(
            DonationPlaybackState.WaitingForServerStart,
            null,
            _queue.Count,
            false,
            string.Empty));
        if (_active.Replay)
        {
            _ = RequestReplayAsync(_active, _lifetimeCancellation.Token);
        }
        else
        {
            ScheduleStartTimeout(_active);
        }
    }

    private async Task RequestReplayAsync(QueueEntry entry, CancellationToken cancellationToken)
    {
        string? error = null;
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_serverStartTimeout);
        try
        {
            await _controller.RepeatDonationAsync(
                entry.Donation,
                timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            error = ServerStartTimeoutError;
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            error = exception.Message;
        }
        _commands.Writer.TryWrite(new ReplayFinished(entry.OperationId, error));
    }

    private void ProcessRequestHide(RequestHide command)
    {
        if (_active is null || Snapshot.State != DonationPlaybackState.Playing)
        {
            Interlocked.Exchange(ref _hideCommandInFlight, 0);
            command.Completion.TrySetResult(false);
            return;
        }

        var donation = _active.Donation;
        Publish(Snapshot with { CanHide = false, Error = string.Empty });
        _ = ExecuteHideAsync(donation, command);
    }

    private async Task ExecuteHideAsync(DonationAlert donation, RequestHide command)
    {
        try
        {
            // Once the command has reached DonationAlerts it must remain single-flight
            // until the server confirms or rejects it. A caller canceling its own wait
            // must not reopen the gate and allow a second skip command to be sent.
            await _controller.SkipDonationAsync(
                donation,
                _lifetimeCancellation.Token).ConfigureAwait(false);
            command.Completion.TrySetResult(true);
        }
        catch (Exception exception) when (IsControlException(exception))
        {
            _commands.Writer.TryWrite(new ControlFailed(donation.Id, exception.Message));
            command.Completion.TrySetResult(false);
        }
        finally
        {
            Interlocked.Exchange(ref _hideCommandInFlight, 0);
        }
    }

    private void ProcessReplayFinished(ReplayFinished command)
    {
        var entry = FindEntryByOperation(command.OperationId);
        if (entry is null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(command.Error))
        {
            if (_active?.OperationId == command.OperationId)
            {
                _halted = true;
                _active = null;
                ReleaseAdmission(entry.Donation.Id);
                Publish(new DonationPlaybackSnapshot(
                    DonationPlaybackState.Error,
                    null,
                    _queue.Count,
                    false,
                    command.Error));
            }
            else
            {
                _ = RemoveQueuedEntryByOperation(command.OperationId);
                ReleaseAdmission(entry.Donation.Id);
                PublishCurrent();
            }
            return;
        }
        if (_active?.OperationId == command.OperationId &&
            Snapshot.State == DonationPlaybackState.WaitingForServerStart)
        {
            BeginEntering(_active);
        }
    }

    private void ProcessStartWaitExpired(StartWaitExpired command)
    {
        if (_active?.OperationId != command.OperationId ||
            Snapshot.State != DonationPlaybackState.WaitingForServerStart)
        {
            return;
        }

        var expired = _active;
        _active = null;
        ReleaseAdmission(expired.Donation.Id);
        Publish(new DonationPlaybackSnapshot(
            DonationPlaybackState.Error,
            null,
            _queue.Count,
            false,
            ServerStartTimeoutError));
        StartNext();
    }

    private void ScheduleStartTimeout(QueueEntry entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_serverStartTimeout, _lifetimeCancellation.Token).ConfigureAwait(false);
                _commands.Writer.TryWrite(new StartWaitExpired(entry.OperationId));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void ProcessServerPlayback(ServerPlayback command)
    {
        if (command.Action == DonationAlertsPlaybackAction.ConnectionRestored)
        {
            _halted = false;
            if (_active is null && Snapshot.State == DonationPlaybackState.Error)
            {
                Publish(new DonationPlaybackSnapshot(
                    _queue.Count > 0 ? DonationPlaybackState.Queued : DonationPlaybackState.Idle,
                    null,
                    _queue.Count,
                    false,
                    string.Empty));
            }
            else if (!string.IsNullOrWhiteSpace(Snapshot.Error))
            {
                Publish(Snapshot with { Error = string.Empty });
            }
            if (_active is null && _queue.Count > 0)
            {
                StartNext();
            }
            return;
        }

        if (command.Action == DonationAlertsPlaybackAction.ConnectionLost)
        {
            ClearBufferedServerSignals();
            _halted = true;
            var disconnected = _active;
            _active = null;
            if (disconnected is not null)
            {
                ReleaseAdmission(disconnected.Donation.Id);
            }
            ClearQueuedEntries();
            Publish(new DonationPlaybackSnapshot(
                DonationPlaybackState.Error,
                disconnected?.Donation ?? Snapshot.CurrentDonation,
                _queue.Count,
                false,
                ConnectionLostError));
            return;
        }

        if (command.Action == DonationAlertsPlaybackAction.Started)
        {
            var entry = TakeEntryForServerStart(command.AlertId);
            if (entry is null)
            {
                BufferEarlyStart(command.AlertId);
                return;
            }
            _halted = false;
            BeginEntering(entry);
            return;
        }

        var terminalEntry = FindEntry(command.AlertId);
        if (terminalEntry is null)
        {
            BufferEarlyTerminal(command.AlertId, command.Action);
            return;
        }
        if (_active?.AlertId != command.AlertId)
        {
            if (terminalEntry.Replay)
            {
                return;
            }
            RemoveQueuedEntry(command.AlertId);
            ReleaseAdmission(terminalEntry.Donation.Id);
            PublishCurrent();
            return;
        }

        if (_active.Replay && Snapshot.State == DonationPlaybackState.WaitingForServerStart)
        {
            return;
        }

        BeginExiting(command.Action);
    }

    private QueueEntry? TakeEntryForServerStart(long alertId)
    {
        if (_active?.AlertId == alertId)
        {
            return _active;
        }

        var selected = RemoveQueuedEntry(alertId);
        if (selected is null)
        {
            return null;
        }
        if (_active is not null)
        {
            PrependQueue(_active);
        }
        _active = selected;
        return selected;
    }

    private void BeginEntering(QueueEntry entry)
    {
        _active = entry;
        Publish(new DonationPlaybackSnapshot(
            DonationPlaybackState.Entering,
            entry.Donation,
            _queue.Count,
            false,
            string.Empty));
        ScheduleTransition(entry.AlertId, entering: true);
    }

    private void BeginExiting(DonationAlertsPlaybackAction action)
    {
        if (_active is null)
        {
            return;
        }
        var state = action == DonationAlertsPlaybackAction.Skipped
            ? DonationPlaybackState.Skipped
            : DonationPlaybackState.Completed;
        Publish(new DonationPlaybackSnapshot(
            DonationPlaybackState.Exiting,
            _active.Donation,
            _queue.Count,
            false,
            string.Empty));
        ScheduleTransition(_active.AlertId, entering: false, state);
    }

    private void ScheduleTransition(
        long alertId,
        bool entering,
        DonationPlaybackState terminalState = DonationPlaybackState.Completed)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var duration = _reduceMotionProvider() ? TimeSpan.Zero : MotionDuration;
                if (duration > TimeSpan.Zero)
                {
                    await Task.Delay(duration, _lifetimeCancellation.Token).ConfigureAwait(false);
                }
                _commands.Writer.TryWrite(new TransitionFinished(alertId, entering, terminalState));
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void ProcessTransitionFinished(TransitionFinished command)
    {
        if (_active?.AlertId != command.AlertId)
        {
            return;
        }
        if (command.Entering)
        {
            if (Snapshot.State == DonationPlaybackState.Entering)
            {
                Publish(new DonationPlaybackSnapshot(
                    DonationPlaybackState.Playing,
                    _active.Donation,
                    _queue.Count,
                    true,
                    string.Empty));
            }
            return;
        }

        var finished = _active;
        _active = null;
        ReleaseAdmission(finished.Donation.Id);
        Publish(new DonationPlaybackSnapshot(
            command.TerminalState,
            null,
            _queue.Count,
            false,
            string.Empty));
        StartNext();
    }

    private void ProcessControlFailed(ControlFailed command)
    {
        if (_active?.Donation.Id != command.DonationId)
        {
            return;
        }
        var stillPlaying = _controller.IsAlertPlaying;
        _halted = !stillPlaying;
        Publish(new DonationPlaybackSnapshot(
            stillPlaying ? DonationPlaybackState.Playing : DonationPlaybackState.Error,
            _active.Donation,
            _queue.Count,
            stillPlaying,
            command.Error));
    }

    private QueueEntry? FindEntry(long alertId)
    {
        if (_active?.AlertId == alertId)
        {
            return _active;
        }
        return _queue.FirstOrDefault(entry => entry.AlertId == alertId);
    }

    private QueueEntry? FindEntryByOperation(long operationId)
    {
        if (_active?.OperationId == operationId)
        {
            return _active;
        }
        return _queue.FirstOrDefault(entry => entry.OperationId == operationId);
    }

    private QueueEntry? RemoveQueuedEntry(long alertId)
    {
        QueueEntry? selected = null;
        var retained = new Queue<QueueEntry>();
        while (_queue.TryDequeue(out var entry))
        {
            if (selected is null && entry.AlertId == alertId)
            {
                selected = entry;
            }
            else
            {
                retained.Enqueue(entry);
            }
        }
        while (retained.TryDequeue(out var entry))
        {
            _queue.Enqueue(entry);
        }
        return selected;
    }

    private QueueEntry? RemoveQueuedEntryByOperation(long operationId)
    {
        QueueEntry? selected = null;
        var retained = new Queue<QueueEntry>();
        while (_queue.TryDequeue(out var entry))
        {
            if (selected is null && entry.OperationId == operationId)
            {
                selected = entry;
            }
            else
            {
                retained.Enqueue(entry);
            }
        }
        while (retained.TryDequeue(out var entry))
        {
            _queue.Enqueue(entry);
        }
        return selected;
    }

    private void PrependQueue(QueueEntry entry)
    {
        var retained = _queue.ToArray();
        _queue.Clear();
        _queue.Enqueue(entry);
        foreach (var queued in retained)
        {
            _queue.Enqueue(queued);
        }
    }

    private void BufferEarlyStart(long alertId)
    {
        if (_earlyTerminals.ContainsKey(alertId) || _earlyStarts.ContainsKey(alertId))
        {
            return;
        }
        var version = ++_bufferVersion;
        _earlyStarts.Add(alertId, version);
        _earlySignalOrder.Enqueue((true, alertId, version));
        TrimBufferedSignals();
    }

    private void BufferEarlyTerminal(long alertId, DonationAlertsPlaybackAction action)
    {
        _earlyStarts.Remove(alertId);
        if (_earlyTerminals.TryGetValue(alertId, out var buffered))
        {
            _earlyTerminals[alertId] = (action, buffered.Version);
        }
        else
        {
            var version = ++_bufferVersion;
            _earlyTerminals.Add(alertId, (action, version));
            _earlySignalOrder.Enqueue((false, alertId, version));
        }
        TrimBufferedSignals();
    }

    private void TrimBufferedSignals()
    {
        while ((_earlyStarts.Count + _earlyTerminals.Count > MaximumBufferedServerSignals ||
                _earlySignalOrder.Count > MaximumBufferedServerSignals * 2) &&
               _earlySignalOrder.TryDequeue(out var expired))
        {
            if (expired.IsStart)
            {
                if (_earlyStarts.TryGetValue(expired.AlertId, out var version) &&
                    version == expired.Version)
                {
                    _earlyStarts.Remove(expired.AlertId);
                }
            }
            else if (_earlyTerminals.TryGetValue(expired.AlertId, out var buffered) &&
                     buffered.Version == expired.Version)
            {
                _earlyTerminals.Remove(expired.AlertId);
            }
        }

        _maximumObservedBufferedServerSignalCount = Math.Max(
            _maximumObservedBufferedServerSignalCount,
            _earlyStarts.Count + _earlyTerminals.Count);
    }

    private void ClearBufferedServerSignals()
    {
        _earlyStarts.Clear();
        _earlyTerminals.Clear();
        _earlySignalOrder.Clear();
    }

    private void ClearQueuedEntries()
    {
        while (_queue.TryDequeue(out var queued))
        {
            ReleaseAdmission(queued.Donation.Id);
        }
    }

    internal int BufferedServerSignalCount => _earlyStarts.Count + _earlyTerminals.Count;
    internal int MaximumObservedBufferedServerSignalCount => _maximumObservedBufferedServerSignalCount;
    internal int RememberedLiveDonationCount
    {
        get
        {
            lock (_admissionGate)
            {
                return _seenLiveIds.Count;
            }
        }
    }

    private void PublishCurrent() => Publish(new DonationPlaybackSnapshot(
        _active is null
            ? _queue.Count > 0 ? DonationPlaybackState.Queued : DonationPlaybackState.Idle
            : Snapshot.State,
        Snapshot.CurrentDonation,
        _queue.Count,
        Snapshot.CanHide,
        Snapshot.Error));

    private void Publish(DonationPlaybackSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        StateChanged?.Invoke(this, snapshot);
    }

    private void ReleaseAdmission(string donationId)
    {
        lock (_admissionGate)
        {
            _admittedIds.Remove(donationId);
        }
    }

    private void RememberLiveDonationId(string donationId)
    {
        _seenLiveIds.Add(donationId);
        _seenLiveIdOrder.Enqueue(donationId);
        while (_seenLiveIdOrder.Count > MaximumRememberedLiveDonationIds)
        {
            _seenLiveIds.Remove(_seenLiveIdOrder.Dequeue());
        }
    }

    private void ForgetLiveDonationId(string donationId)
    {
        if (!_seenLiveIds.Remove(donationId))
        {
            return;
        }
        var retained = _seenLiveIdOrder.Where(id =>
            !string.Equals(id, donationId, StringComparison.Ordinal)).ToArray();
        _seenLiveIdOrder.Clear();
        foreach (var id in retained)
        {
            _seenLiveIdOrder.Enqueue(id);
        }
    }

    private static bool IsControlException(Exception exception) =>
        exception is HttpRequestException or IOException or InvalidDataException or
            InvalidOperationException or OperationCanceledException or TimeoutException or
            WebSocketException or JsonException;

    private abstract record Command;
    private sealed record Enqueue(QueueEntry Entry) : Command;
    private sealed record ServerPlayback(DonationAlertsPlaybackAction Action, long AlertId) : Command;
    private sealed record ReplayFinished(long OperationId, string? Error) : Command;
    private sealed record StartWaitExpired(long OperationId) : Command;
    private sealed record TransitionFinished(
        long AlertId,
        bool Entering,
        DonationPlaybackState TerminalState) : Command;
    private sealed record ControlFailed(string DonationId, string Error) : Command;
    private sealed record RequestHide(TaskCompletionSource<bool> Completion) : Command;
    private sealed record ClearPendingQueue : Command;
    private sealed record QueueEntry(
        DonationAlert Donation,
        long AlertId,
        bool Replay,
        long OperationId);
}
