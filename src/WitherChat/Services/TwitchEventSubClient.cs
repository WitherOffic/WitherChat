using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class TwitchEventSubClient : IAsyncDisposable
{
    private const string CustomRedemptionSubscription = "channel.channel_points_custom_reward_redemption.add";
    private const string AutomaticRedemptionSubscription = "channel.channel_points_automatic_reward_redemption.add";
    private const int MaxEventPayloadBytes = 1024 * 1024;
    private const string WebSocketUrl = "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30";
    private readonly TwitchApiClient _apiClient;
    private readonly FileLogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HttpMessageInvoker _webSocketInvoker;
    private RunContext? _currentRun;
    private long _nextGeneration;

    public event EventHandler<ChatMessageModel>? MessageReceived;
    public event EventHandler<EventSubConnectionStatusEventArgs>? StatusChanged;
    public event EventHandler? ChannelPointsAuthorizationRequired;
    public event EventHandler<ChannelPointsCapabilityEventArgs>? ChannelPointsCapabilityChanged;
    public event EventHandler<ChatMessageDeletedEventArgs>? ChatMessageDeleted;
    public event EventHandler<UserMessagesClearedEventArgs>? UserMessagesCleared;
    public event EventHandler<ChannelUserBannedEventArgs>? UserBanned;
    public event EventHandler<ChannelUserUnbannedEventArgs>? UserUnbanned;
    public event EventHandler<UnbanRequestEntry>? UnbanRequestCreated;
    public event EventHandler<UnbanRequestEntry>? UnbanRequestResolved;
    public event EventHandler<HeldAutoModMessage>? AutoModMessageHeld;
    public event EventHandler<AutoModMessageUpdatedEventArgs>? AutoModMessageUpdated;
    public event EventHandler<SharedChatSessionEventArgs>? SharedChatSessionChanged;

    public TwitchEventSubClient(TwitchApiClient apiClient, FileLogger logger)
    {
        _apiClient = apiClient;
        _logger = logger;
        _webSocketInvoker = new HttpMessageInvoker(
            new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                }
            },
            disposeHandler: true);
    }

    public async Task TrySubscribeChatMessageAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return;
        }

        RunContext? context;
        string activeSessionId;
        CancellationToken contextCancellationToken;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                context = _currentRun;
            }

            if (context is null)
            {
                return;
            }

            lock (context.SubscriptionSync)
            {
                context.RequestedChatBroadcasters.Add(broadcasterId);
                if (context.HandoffInProgress != 0)
                {
                    return;
                }

                activeSessionId = context.ActiveSessionId;
                contextCancellationToken = context.Cancellation.Token;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            contextCancellationToken,
            cancellationToken);
        await SubscribeChatMessageAsync(
            context,
            activeSessionId,
            broadcasterId,
            linkedCancellation.Token).ConfigureAwait(false);
        await SubscribeSharedChatAsync(
            context,
            activeSessionId,
            broadcasterId,
            linkedCancellation.Token).ConfigureAwait(false);
    }

    public async Task TrySubscribeChannelPointsAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(broadcasterId) || !_apiClient.HasChannelPointsScope)
        {
            return;
        }

        RunContext? context;
        string activeSessionId;
        CancellationToken contextCancellationToken;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                context = _currentRun;
            }

            if (context is null)
            {
                return;
            }

            lock (context.SubscriptionSync)
            {
                context.RequestedChannelPointsBroadcasters.Add(broadcasterId);
                if (context.HandoffInProgress != 0)
                {
                    return;
                }

                activeSessionId = context.ActiveSessionId;
                contextCancellationToken = context.Cancellation.Token;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            contextCancellationToken,
            cancellationToken);
        try
        {
            await SubscribeChannelPointsAsync(
                context,
                activeSessionId,
                broadcasterId,
                linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
    }

    public async Task StartAsync(string broadcasterId, string chattingUserId, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var context = new RunContext(
                Interlocked.Increment(ref _nextGeneration),
                broadcasterId,
                chattingUserId,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));

            lock (_gate)
            {
                _currentRun = context;
                context.RunTask = Task.Run(() => RunLoopAsync(context), CancellationToken.None);
            }

            _logger.Info($"Starting chat connect: broadcaster_id={broadcasterId}, user_id={chattingUserId}");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RemoveBroadcasterAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RunContext? current;
            lock (_gate)
            {
                current = _currentRun;
            }

            if (current is null ||
                string.Equals(current.BroadcasterId, broadcasterId, StringComparison.Ordinal))
            {
                return;
            }

            string[] requestedChatBroadcasters;
            string[] requestedChannelPointsBroadcasters;
            string[] requestedModerationBroadcasters;
            lock (current.SubscriptionSync)
            {
                requestedChatBroadcasters = current.RequestedChatBroadcasters
                    .Where(id => !string.Equals(id, broadcasterId, StringComparison.Ordinal))
                    .ToArray();
                requestedChannelPointsBroadcasters = current.RequestedChannelPointsBroadcasters
                    .Where(id => !string.Equals(id, broadcasterId, StringComparison.Ordinal))
                    .ToArray();
                requestedModerationBroadcasters = current.RequestedModerationBroadcasters
                    .Where(id => !string.Equals(id, broadcasterId, StringComparison.Ordinal))
                    .ToArray();
            }

            var primaryBroadcasterId = current.BroadcasterId;
            var chattingUserId = current.ChattingUserId;
            await StopCoreAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var replacement = new RunContext(
                Interlocked.Increment(ref _nextGeneration),
                primaryBroadcasterId,
                chattingUserId,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            lock (replacement.SubscriptionSync)
            {
                replacement.RequestedChatBroadcasters.UnionWith(requestedChatBroadcasters);
                replacement.RequestedChannelPointsBroadcasters.UnionWith(requestedChannelPointsBroadcasters);
                replacement.RequestedModerationBroadcasters.UnionWith(requestedModerationBroadcasters);
            }

            lock (_gate)
            {
                _currentRun = replacement;
                replacement.RunTask = Task.Run(() => RunLoopAsync(replacement), CancellationToken.None);
            }

            _logger.Info($"EventSub subscriptions restarted after removing broadcaster_id={broadcasterId}.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> WaitForInitialConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_gate)
        {
            task = _currentRun?.InitialConnection.Task;
        }

        if (task is null)
        {
            return false;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        RunContext? context;
        lock (_gate)
        {
            context = _currentRun;
            _currentRun = null;
        }

        if (context is null)
        {
            return;
        }

        context.InitialConnection.TrySetCanceled();
        context.Cancellation.Cancel();

        ClientWebSocket? socket;
        lock (_gate)
        {
            socket = context.Socket;
        }

        try
        {
            socket?.Abort();
        }
        catch (ObjectDisposedException)
        {
            // The receive loop completed between capturing and aborting the socket.
        }

        try
        {
            await context.RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            // Expected while stopping the active run.
        }
        catch (Exception ex)
        {
            _logger.Error($"EventSub run {context.Generation} failed while stopping", ex);
        }

        Task[] backgroundTasks;
        lock (context.SubscriptionSync)
        {
            backgroundTasks = context.BackgroundTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"EventSub background subscriptions stopped with {ex.GetType().Name}");
        }

        context.SubscriptionGate.Dispose();
        context.Cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _webSocketInvoker.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task RunLoopAsync(RunContext context)
    {
        var cancellationToken = context.Cancellation.Token;
        var nextUrl = WebSocketUrl;
        var needsSubscription = true;
        var attempt = 0;
        Task<ListenResult>? activeConnectionTask = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ListenResult result;
                if (activeConnectionTask is null)
                {
                    RaiseStatus(
                        context,
                        attempt == 0 ? ChannelConnectionState.Connecting : ChannelConnectionState.Reconnecting);
                    result = await ConnectAndListenAsync(
                        context,
                        nextUrl,
                        needsSubscription,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var connectionTask = activeConnectionTask;
                    activeConnectionTask = null;
                    result = await connectionTask.ConfigureAwait(false);
                }

                if (result.HandoffTask is not null)
                {
                    activeConnectionTask = result.HandoffTask;
                    nextUrl = WebSocketUrl;
                    needsSubscription = false;
                    attempt = 0;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(result.ReconnectUrl))
                {
                    nextUrl = result.ReconnectUrl;
                    needsSubscription = result.NeedsSubscription;
                    attempt = 0;
                    continue;
                }

                nextUrl = WebSocketUrl;
                needsSubscription = true;
                attempt++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (CoreChatSubscriptionRevokedException ex)
            {
                _logger.Warn(
                    $"Core chat EventSub subscription revoked: type={ex.SubscriptionType}, status={ex.Status}");
                context.InitialConnection.TrySetException(ex);
                RaiseStatus(
                    context,
                    ChannelConnectionState.Error,
                    errorCode: "subscription_revoked");
                return;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (context.SubscriptionSync)
                {
                    context.HandoffInProgress = 0;
                }
                _logger.Error("EventSub connection failed", ex);
                if (Volatile.Read(ref context.ConnectedReported) == 0)
                {
                    RaiseStatus(context, ChannelConnectionState.Error, ex.Message);
                }
                else
                {
                    RaiseStatus(context, ChannelConnectionState.Reconnecting, ex.Message);
                }

                nextUrl = WebSocketUrl;
                needsSubscription = true;
                attempt++;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, attempt * 3)));
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (activeConnectionTask is not null)
        {
            await ObserveConnectionCompletionAsync(activeConnectionTask).ConfigureAwait(false);
        }

        context.InitialConnection.TrySetCanceled(cancellationToken);
        RaiseStatus(context, ChannelConnectionState.Disconnected);
    }

    private async Task<ListenResult> ConnectAndListenAsync(
        RunContext context,
        string url,
        bool needsSubscription,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool>? welcomeSignal = null)
    {
        if (welcomeSignal is null)
        {
            lock (context.SubscriptionSync)
            {
                context.ActiveSessionId = string.Empty;
            }
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var socketUri) ||
            socketUri.Scheme != "wss" ||
            !socketUri.Host.Equals("eventsub.wss.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(socketUri.UserInfo))
        {
            throw new InvalidOperationException(
                LocalizationService.Get(LocalizationService.CurrentLanguage, "InvalidEventSubReconnect"));
        }

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        Task<ListenResult>? handoffTask = null;
        TaskCompletionSource<bool>? handoffWelcomeSignal = null;

        lock (_gate)
        {
            if (!ReferenceEquals(_currentRun, context))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            context.Socket = socket;
        }

        try
        {
            _logger.Info("WebSocket connecting to Twitch EventSub.");
            await socket.ConnectAsync(socketUri, _webSocketInvoker, cancellationToken).ConfigureAwait(false);
            _logger.Info("WebSocket connected to Twitch EventSub.");
            var keepaliveSeconds = 30;
            var subscribed = !needsSubscription;
            var localSessionId = string.Empty;

            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (handoffTask is not null && handoffWelcomeSignal?.Task.IsCompletedSuccessfully == true)
                {
                    return new ListenResult(null, false, handoffTask);
                }

                string? text;
                using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    receiveCts.CancelAfter(TimeSpan.FromSeconds(keepaliveSeconds + 5));
                    try
                    {
                        var receiveTask = ReceiveTextAsync(socket, receiveCts.Token);
                        if (handoffTask is null || handoffWelcomeSignal is null)
                        {
                            text = await receiveTask.ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.WhenAny(
                                receiveTask,
                                handoffWelcomeSignal.Task,
                                handoffTask).ConfigureAwait(false);
                            if (handoffWelcomeSignal.Task.IsCompletedSuccessfully)
                            {
                                receiveCts.Cancel();
                                await ObserveCanceledReceiveAsync(receiveTask).ConfigureAwait(false);
                                return new ListenResult(null, false, handoffTask);
                            }
                            else if (receiveTask.IsCompletedSuccessfully)
                            {
                                text = receiveTask.Result;
                            }
                            else if (handoffTask.IsCompleted)
                            {
                                receiveCts.Cancel();
                                await ObserveCanceledReceiveAsync(receiveTask).ConfigureAwait(false);
                                try
                                {
                                    _ = await handoffTask.ConfigureAwait(false);
                                    _logger.Warn("EventSub handoff connection ended before session_welcome; keeping the old socket active.");
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                                {
                                    _logger.Warn($"EventSub handoff failed before session_welcome: {ex.GetType().Name}");
                                }

                                handoffTask = null;
                                handoffWelcomeSignal = null;
                                lock (_gate)
                                {
                                    if (ReferenceEquals(_currentRun, context))
                                    {
                                        context.Socket = socket;
                                    }
                                }
                                lock (context.SubscriptionSync)
                                {
                                    context.HandoffInProgress = 0;
                                    context.ActiveSessionId = localSessionId;
                                }
                                continue;
                            }
                            else
                            {
                                text = await receiveTask.ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException("Twitch EventSub keepalive timeout.");
                    }
                }

                if (text is null)
                {
                    if (handoffTask is not null)
                    {
                        return new ListenResult(null, false, handoffTask);
                    }

                    return new ListenResult(null, true);
                }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var metadata = root.GetProperty("metadata");
                var messageType = GetString(metadata, "message_type");

                switch (messageType)
                {
                    case "session_welcome":
                        {
                            var session = root.GetProperty("payload").GetProperty("session");
                            var sessionId = GetString(session, "id");
                            localSessionId = sessionId;
                            keepaliveSeconds = GetInt(session, "keepalive_timeout_seconds") ?? keepaliveSeconds;
                            _logger.Info($"WebSocket session_welcome received: session_id={sessionId}");

                            if (needsSubscription && !subscribed)
                            {
                                await ResetSubscriptionKeysAsync(context, cancellationToken).ConfigureAwait(false);
                            }

                            string[] chatBroadcasters;
                            string[] channelPointsBroadcasters;
                            string[] moderationBroadcasters;
                            lock (context.SubscriptionSync)
                            {
                                context.ActiveSessionId = sessionId;
                                context.HandoffInProgress = 0;
                                context.RequestedChannelPointsBroadcasters.Add(context.BroadcasterId);
                                chatBroadcasters = context.RequestedChatBroadcasters.ToArray();
                                channelPointsBroadcasters = context.RequestedChannelPointsBroadcasters.ToArray();
                                moderationBroadcasters = context.RequestedModerationBroadcasters.ToArray();
                            }

                            var chatSubscriptionsReady = await SubscribeRequestedChatMessagesAsync(
                                context,
                                sessionId,
                                chatBroadcasters,
                                cancellationToken).ConfigureAwait(false);
                            if (!chatSubscriptionsReady)
                            {
                                throw new InvalidOperationException(
                                    LocalizationService.Get(
                                        LocalizationService.CurrentLanguage,
                                        "EventSubSubscriptionFailed"));
                            }

                            subscribed = true;
                            TrackBackgroundTask(
                                context,
                                SubscribeRequestedSharedChatAsync(
                                    context,
                                    sessionId,
                                    chatBroadcasters,
                                    cancellationToken));

                            if (_apiClient.HasChannelPointsScope)
                            {
                                TrackBackgroundTask(
                                    context,
                                    SubscribeRequestedChannelPointsAsync(
                                        context,
                                        sessionId,
                                        channelPointsBroadcasters,
                                        cancellationToken));
                            }

                            TrackBackgroundTask(
                                context,
                                SubscribeRequestedModerationAsync(
                                    context,
                                    sessionId,
                                    moderationBroadcasters,
                                    context.ChattingUserId,
                                    cancellationToken));

                            MarkConnected(context);
                            welcomeSignal?.TrySetResult(true);
                            break;
                        }

                    case "session_keepalive":
                        break;

                    case "notification":
                        HandleNotification(context, root, metadata);
                        break;

                    case "session_reconnect":
                        {
                            var reconnectUrl = GetString(root.GetProperty("payload").GetProperty("session"), "reconnect_url");
                            if (!string.IsNullOrWhiteSpace(reconnectUrl))
                            {
                                if (handoffTask is null)
                                {
                                    RaiseStatus(context, ChannelConnectionState.Reconnecting);
                                    lock (context.SubscriptionSync)
                                    {
                                        context.HandoffInProgress = 1;
                                    }
                                    handoffWelcomeSignal = new TaskCompletionSource<bool>(
                                        TaskCreationOptions.RunContinuationsAsynchronously);
                                    handoffTask = ConnectAndListenAsync(
                                        context,
                                        reconnectUrl,
                                        needsSubscription: false,
                                        cancellationToken,
                                        handoffWelcomeSignal);
                                }
                                else
                                {
                                    _logger.Warn("Ignoring a duplicate EventSub reconnect request while handoff is already active.");
                                }

                                break;
                            }

                            return handoffTask is null
                                ? new ListenResult(null, true)
                                : new ListenResult(null, false, handoffTask);
                        }

                    case "revocation":
                        {
                            var payload = root.GetProperty("payload");
                            var subscription = payload.GetProperty("subscription");
                            var revokedType = GetString(subscription, "type");
                            if (string.IsNullOrWhiteSpace(revokedType))
                            {
                                revokedType = GetString(metadata, "subscription_type");
                            }

                            var revokedStatus = GetString(subscription, "status");
                            var revokedBroadcasterId = subscription.TryGetProperty("condition", out var condition)
                                ? GetString(condition, "broadcaster_user_id")
                                : string.Empty;
                            ForgetRevokedSubscriptionKeys(context, revokedType, revokedBroadcasterId);

                            if (IsChannelPointsSubscription(revokedType))
                            {
                                _logger.Warn(
                                    $"Channel Points EventSub subscription revoked: type={revokedType}, status={revokedStatus}");
                                if (IsCurrent(context))
                                {
                                    ChannelPointsCapabilityChanged?.Invoke(
                                        this,
                                        new ChannelPointsCapabilityEventArgs(
                                            string.IsNullOrWhiteSpace(revokedBroadcasterId)
                                                ? context.BroadcasterId
                                                : revokedBroadcasterId,
                                            available: false));
                                }

                                if (RequiresAuthorization(revokedStatus) &&
                                    Interlocked.Exchange(ref context.ChannelPointsAuthorizationReported, 1) == 0 &&
                                    IsCurrent(context))
                                {
                                    ChannelPointsAuthorizationRequired?.Invoke(this, EventArgs.Empty);
                                }
                            }
                            else if (string.Equals(revokedType, "channel.chat.message", StringComparison.Ordinal))
                            {
                                if (string.IsNullOrWhiteSpace(revokedBroadcasterId) ||
                                    string.Equals(revokedBroadcasterId, context.BroadcasterId, StringComparison.Ordinal))
                                {
                                    throw new CoreChatSubscriptionRevokedException(revokedType, revokedStatus);
                                }

                                _logger.Warn(
                                    $"A secondary channel.chat.message subscription was revoked: " +
                                    $"broadcaster_id={revokedBroadcasterId}, status={revokedStatus}");
                            }
                            else if (revokedType.StartsWith("channel.shared_chat.", StringComparison.Ordinal))
                            {
                                _logger.Warn(
                                    $"A Shared Chat subscription was revoked: type={revokedType}, status={revokedStatus}");
                                if (IsCurrent(context) && !string.IsNullOrWhiteSpace(revokedBroadcasterId))
                                {
                                    SharedChatSessionChanged?.Invoke(this, new SharedChatSessionEventArgs(
                                        revokedBroadcasterId,
                                        string.Empty,
                                        string.Empty,
                                        string.Empty,
                                        string.Empty,
                                        [],
                                        false));
                                }
                            }
                            else if (revokedType.StartsWith("automod.", StringComparison.Ordinal) ||
                                     revokedType is "channel.ban" or "channel.unban" ||
                                     revokedType.StartsWith("channel.unban_request.", StringComparison.Ordinal) ||
                                     revokedType.StartsWith("channel.chat.message_delete", StringComparison.Ordinal) ||
                                     revokedType.StartsWith("channel.chat.clear_user_messages", StringComparison.Ordinal))
                            {
                                _logger.Warn(
                                    $"A moderation subscription was revoked: type={revokedType}, status={revokedStatus}");
                            }
                            else
                            {
                                _logger.Warn(
                                    $"An EventSub subscription was revoked: type={revokedType}, status={revokedStatus}");
                            }
                            break;
                        }
                }
            }

            return handoffTask is null
                ? new ListenResult(null, true)
                : new ListenResult(null, false, handoffTask);
        }
        catch (CoreChatSubscriptionRevokedException)
        {
            if (handoffTask is not null)
            {
                context.Cancellation.Cancel();
                await ObserveConnectionCompletionAsync(handoffTask).ConfigureAwait(false);
            }

            throw;
        }
        catch (OperationCanceledException) when (handoffTask is not null && cancellationToken.IsCancellationRequested)
        {
            await ObserveConnectionCompletionAsync(handoffTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception) when (handoffTask is not null && cancellationToken.IsCancellationRequested)
        {
            await ObserveConnectionCompletionAsync(handoffTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (
            handoffTask is not null &&
            ex is not CoreChatSubscriptionRevokedException &&
            !cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"Old EventSub socket ended during handoff: {ex.GetType().Name}");
            return new ListenResult(null, false, handoffTask);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(context.Socket, socket))
                {
                    context.Socket = null;
                }
            }
        }
    }

    private void HandleNotification(RunContext context, JsonElement root, JsonElement metadata)
    {
        var subscriptionType = GetString(metadata, "subscription_type");
        if (subscriptionType.StartsWith("channel.shared_chat.", StringComparison.Ordinal))
        {
            RaiseSharedChatSessionChanged(root, subscriptionType);
            return;
        }
        if (string.Equals(subscriptionType, "channel.chat.message_delete", StringComparison.Ordinal))
        {
            var moderationEvent = root.GetProperty("payload").GetProperty("event");
            ChatMessageDeleted?.Invoke(this, new ChatMessageDeletedEventArgs(
                GetString(moderationEvent, "broadcaster_user_id"),
                GetString(moderationEvent, "message_id"),
                GetString(moderationEvent, "target_user_id")));
            return;
        }

        if (string.Equals(subscriptionType, "channel.chat.clear_user_messages", StringComparison.Ordinal))
        {
            var moderationEvent = root.GetProperty("payload").GetProperty("event");
            UserMessagesCleared?.Invoke(this, new UserMessagesClearedEventArgs(
                GetString(moderationEvent, "broadcaster_user_id"),
                GetString(moderationEvent, "target_user_id")));
            return;
        }

        if (string.Equals(subscriptionType, "channel.ban", StringComparison.Ordinal))
        {
            var banEvent = root.GetProperty("payload").GetProperty("event");
            var startedAt = ParseTimestamp(GetString(banEvent, "banned_at"));
            var endsAtText = GetString(banEvent, "ends_at");
            var endsAt = DateTimeOffset.TryParse(endsAtText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedEnd)
                ? parsedEnd
                : (DateTimeOffset?)null;
            UserBanned?.Invoke(this, new ChannelUserBannedEventArgs(
                GetString(banEvent, "broadcaster_user_id"),
                GetString(banEvent, "user_id"),
                GetString(banEvent, "user_login"),
                GetString(banEvent, "user_name"),
                GetString(banEvent, "moderator_user_id"),
                GetString(banEvent, "moderator_user_name"),
                GetString(banEvent, "reason"),
                startedAt,
                endsAt,
                banEvent.TryGetProperty("is_permanent", out var permanent) && permanent.ValueKind == JsonValueKind.True));
            return;
        }

        if (string.Equals(subscriptionType, "channel.unban", StringComparison.Ordinal))
        {
            var unbanEvent = root.GetProperty("payload").GetProperty("event");
            UserUnbanned?.Invoke(this, new ChannelUserUnbannedEventArgs(
                GetString(unbanEvent, "broadcaster_user_id"),
                GetString(unbanEvent, "user_id"),
                GetString(unbanEvent, "user_login"),
                GetString(unbanEvent, "user_name"),
                GetString(unbanEvent, "moderator_user_id"),
                GetString(unbanEvent, "moderator_user_name")));
            return;
        }

        if (string.Equals(subscriptionType, "channel.unban_request.create", StringComparison.Ordinal))
        {
            var request = ParseUnbanRequest(root, resolved: false);
            if (request is not null)
            {
                UnbanRequestCreated?.Invoke(this, request);
            }
            return;
        }

        if (string.Equals(subscriptionType, "channel.unban_request.resolve", StringComparison.Ordinal))
        {
            var request = ParseUnbanRequest(root, resolved: true);
            if (request is not null)
            {
                UnbanRequestResolved?.Invoke(this, request);
            }
            return;
        }

        if (string.Equals(subscriptionType, "automod.message.hold", StringComparison.Ordinal))
        {
            var held = ParseHeldAutoModMessage(root, metadata);
            if (held is not null)
            {
                AutoModMessageHeld?.Invoke(this, held);
            }
            return;
        }

        if (string.Equals(subscriptionType, "automod.message.update", StringComparison.Ordinal))
        {
            var moderationEvent = root.GetProperty("payload").GetProperty("event");
            var status = GetString(moderationEvent, "status").ToUpperInvariant() switch
            {
                "ALLOWED" => HeldAutoModStatus.Approved,
                "DENIED" => HeldAutoModStatus.Denied,
                "EXPIRED" => HeldAutoModStatus.Expired,
                _ => HeldAutoModStatus.Pending
            };
            AutoModMessageUpdated?.Invoke(this, new AutoModMessageUpdatedEventArgs(
                GetString(moderationEvent, "broadcaster_user_id"),
                GetString(moderationEvent, "message_id"),
                status));
            return;
        }

        if (IsChannelPointsSubscription(subscriptionType))
        {
            var redemption = ParseChannelPointsRedemption(root, metadata, subscriptionType);
            if (redemption is not null)
            {
                RaiseMessage(context, redemption);
            }
            return;
        }

        if (!string.Equals(subscriptionType, "channel.chat.message", StringComparison.Ordinal))
        {
            return;
        }

        var payload = root.GetProperty("payload");
        var evt = payload.GetProperty("event");
        var timestamp = ParseTimestamp(GetString(metadata, "message_timestamp"));
        var sourceBroadcasterId = GetString(evt, "source_broadcaster_user_id");
        var sourceBadgeProperty = string.IsNullOrWhiteSpace(sourceBroadcasterId) ? "badges" : "source_badges";
        var badges = new ObservableCollection<BadgeModel>();

        var hasBadgeArray = evt.TryGetProperty(sourceBadgeProperty, out var badgeArray) &&
                            badgeArray.ValueKind == JsonValueKind.Array;
        if (!hasBadgeArray && !string.Equals(sourceBadgeProperty, "badges", StringComparison.Ordinal))
        {
            hasBadgeArray = evt.TryGetProperty("badges", out badgeArray) &&
                            badgeArray.ValueKind == JsonValueKind.Array;
        }

        if (hasBadgeArray)
        {
            foreach (var badge in badgeArray.EnumerateArray())
            {
                badges.Add(new BadgeModel
                {
                    SetId = GetString(badge, "set_id"),
                    Id = GetString(badge, "id"),
                    Info = GetString(badge, "info")
                });
            }
        }

        var messageText = string.Empty;
        var parts = new ObservableCollection<ChatMessagePartModel>();
        if (evt.TryGetProperty("message", out var message) &&
            message.TryGetProperty("text", out var textElement))
        {
            messageText = textElement.GetString() ?? string.Empty;

            if (message.TryGetProperty("fragments", out var fragments) && fragments.ValueKind == JsonValueKind.Array)
            {
                foreach (var fragment in fragments.EnumerateArray())
                {
                    var fragmentText = GetString(fragment, "text");
                    var type = GetString(fragment, "type");
                    if (string.Equals(type, "emote", StringComparison.OrdinalIgnoreCase) &&
                        fragment.TryGetProperty("emote", out var emote) &&
                        emote.ValueKind == JsonValueKind.Object)
                    {
                        var isAnimated = emote.TryGetProperty("format", out var formats) &&
                                         formats.ValueKind == JsonValueKind.Array &&
                                         formats.EnumerateArray().Any(format =>
                                             string.Equals(format.GetString(), "animated", StringComparison.OrdinalIgnoreCase));
                        parts.Add(ChatMessagePartModel.TwitchEmote(fragmentText, GetString(emote, "id"), isAnimated));
                    }
                    else if (!string.IsNullOrEmpty(fragmentText))
                    {
                        parts.Add(ChatMessagePartModel.TextPart(fragmentText));
                    }
                }
            }
        }

        if (parts.Count == 0 && !string.IsNullOrEmpty(messageText))
        {
            parts.Add(ChatMessagePartModel.TextPart(messageText));
        }

        var replyParentMessageId = string.Empty;
        var replyParentUserId = string.Empty;
        var replyParentUserLogin = string.Empty;
        var replyParentDisplayName = string.Empty;
        var replyParentMessageBody = string.Empty;
        if (evt.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.Object)
        {
            replyParentMessageId = GetString(reply, "parent_message_id");
            replyParentUserId = GetString(reply, "parent_user_id");
            replyParentUserLogin = GetString(reply, "parent_user_login");
            replyParentDisplayName = GetString(reply, "parent_user_name");
            replyParentMessageBody = GetString(reply, "parent_message_body");
        }

        var chatMessage = new ChatMessageModel
        {
            Id = GetString(evt, "message_id"),
            MessageId = GetString(evt, "message_id"),
            ReplyParentMessageId = replyParentMessageId,
            ReplyParentUserId = replyParentUserId,
            ReplyParentUserLogin = replyParentUserLogin,
            ReplyParentDisplayName = replyParentDisplayName,
            ReplyParentMessageBody = replyParentMessageBody,
            MessageType = GetString(evt, "message_type"),
            Timestamp = timestamp,
            BroadcasterId = GetString(evt, "broadcaster_user_id"),
            ChannelLogin = GetString(evt, "broadcaster_user_login"),
            RoomId = GetString(evt, "broadcaster_user_id"),
            SourceBroadcasterId = sourceBroadcasterId,
            SourceChannelLogin = GetString(evt, "source_broadcaster_user_login"),
            SourceChannelDisplayName = GetString(evt, "source_broadcaster_user_name"),
            BadgeBroadcasterId = string.IsNullOrWhiteSpace(sourceBroadcasterId)
                ? GetString(evt, "broadcaster_user_id")
                : sourceBroadcasterId,
            UserId = GetString(evt, "chatter_user_id"),
            Login = GetString(evt, "chatter_user_login"),
            DisplayName = GetString(evt, "chatter_user_name"),
            Text = messageText,
            Color = GetString(evt, "color"),
            CustomRewardId = GetString(evt, "channel_points_custom_reward_id"),
            Badges = badges,
            Parts = parts
        };

        RaiseMessage(context, chatMessage);
    }

    private static HeldAutoModMessage? ParseHeldAutoModMessage(JsonElement root, JsonElement metadata)
    {
        var evt = root.GetProperty("payload").GetProperty("event");
        var messageId = GetString(evt, "message_id");
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        var text = string.Empty;
        if (evt.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            text = GetString(message, "text");
        }

        var category = string.Empty;
        var level = 0;
        if (evt.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.Object)
        {
            category = GetString(reason, "category");
            level = GetInt(reason, "level") ?? 0;
            if (reason.TryGetProperty("automod", out var automod) && automod.ValueKind == JsonValueKind.Object)
            {
                category = string.IsNullOrWhiteSpace(category) ? GetString(automod, "category") : category;
                level = level == 0 ? GetInt(automod, "level") ?? 0 : level;
            }
        }

        return new HeldAutoModMessage
        {
            MessageId = messageId,
            BroadcasterId = GetString(evt, "broadcaster_user_id"),
            UserId = GetString(evt, "user_id"),
            UserLogin = GetString(evt, "user_login"),
            UserDisplayName = GetString(evt, "user_name"),
            MessageText = text,
            Category = category,
            Level = level,
            HeldAt = ParseTimestamp(GetString(metadata, "message_timestamp")),
            Status = HeldAutoModStatus.Pending
        };
    }

    private void RaiseSharedChatSessionChanged(JsonElement root, string subscriptionType)
    {
        var evt = root.GetProperty("payload").GetProperty("event");
        var participants = new List<SharedChatParticipant>();
        if (evt.TryGetProperty("participants", out var participantArray) &&
            participantArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var participant in participantArray.EnumerateArray())
            {
                participants.Add(new SharedChatParticipant(
                    GetString(participant, "broadcaster_user_id"),
                    GetString(participant, "broadcaster_user_login"),
                    GetString(participant, "broadcaster_user_name")));
            }
        }

        SharedChatSessionChanged?.Invoke(this, new SharedChatSessionEventArgs(
            GetString(evt, "broadcaster_user_id"),
            GetString(evt, "session_id"),
            GetString(evt, "host_broadcaster_user_id"),
            GetString(evt, "host_broadcaster_user_login"),
            GetString(evt, "host_broadcaster_user_name"),
            participants,
            !string.Equals(subscriptionType, "channel.shared_chat.end", StringComparison.Ordinal)));
    }

    public async Task TrySubscribeModerationAsync(
        string broadcasterId,
        string moderatorUserId,
        bool canModerate,
        CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        moderatorUserId = (moderatorUserId ?? string.Empty).Trim();
        if (!canModerate || string.IsNullOrWhiteSpace(broadcasterId) || string.IsNullOrWhiteSpace(moderatorUserId))
        {
            return;
        }

        RunContext? context;
        string activeSessionId;
        CancellationToken contextCancellationToken;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                context = _currentRun;
            }

            if (context is null)
            {
                return;
            }

            lock (context.SubscriptionSync)
            {
                context.RequestedModerationBroadcasters.Add(broadcasterId);
                if (context.HandoffInProgress != 0)
                {
                    return;
                }

                activeSessionId = context.ActiveSessionId;
                contextCancellationToken = context.Cancellation.Token;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        if (string.IsNullOrWhiteSpace(activeSessionId))
        {
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(contextCancellationToken, cancellationToken);
        await SubscribeModerationAsync(context, activeSessionId, broadcasterId, moderatorUserId, linkedCancellation.Token).ConfigureAwait(false);
    }

    private static void ForgetSubscriptionKey(RunContext context, string key)
    {
        lock (context.SubscriptionSync)
        {
            context.CreatedSubscriptionKeys.Remove(key);
        }
    }

    private static void ForgetRevokedSubscriptionKeys(
        RunContext context,
        string subscriptionType,
        string broadcasterId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
        {
            return;
        }

        lock (context.SubscriptionSync)
        {
            context.CreatedSubscriptionKeys.RemoveWhere(key =>
                (key.StartsWith(subscriptionType + "\n", StringComparison.Ordinal) ||
                 key.StartsWith(subscriptionType + ":", StringComparison.Ordinal)) &&
                (string.IsNullOrWhiteSpace(broadcasterId) ||
                 key.StartsWith(subscriptionType + "\n" + broadcasterId, StringComparison.Ordinal) ||
                 key.Contains(":" + broadcasterId + ":", StringComparison.Ordinal)));
        }
    }

    private static bool RequiresAuthorization(string status) =>
        status is "authorization_revoked" or "user_removed" or "moderator_removed" or "chat_user_banned";

    private static bool IsActiveSession(RunContext context, string sessionId)
    {
        lock (context.SubscriptionSync)
        {
            return string.Equals(context.ActiveSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    private static async Task ResetSubscriptionKeysAsync(
        RunContext context,
        CancellationToken cancellationToken)
    {
        await context.SubscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (context.SubscriptionSync)
            {
                context.CreatedSubscriptionKeys.Clear();
            }
        }
        finally
        {
            context.SubscriptionGate.Release();
        }
    }

    private async Task<bool> SubscribeChatMessageAsync(
        RunContext context,
        string sessionId,
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return false;
        }

        var key = $"channel.chat.message\n{broadcasterId}\n{context.ChattingUserId}";
        await context.SubscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActiveSession(context, sessionId))
            {
                return false;
            }

            lock (context.SubscriptionSync)
            {
                if (!context.CreatedSubscriptionKeys.Add(key))
                {
                    _logger.Info($"EventSub chat subscription: broadcasterId={broadcasterId}, status=already-exists, http=none");
                    return true;
                }
            }

            var keepSubscriptionKey = false;
            try
            {
                await _apiClient.CreateChatMessageSubscriptionAsync(
                    sessionId,
                    broadcasterId,
                    context.ChattingUserId,
                    cancellationToken).ConfigureAwait(false);
                keepSubscriptionKey = true;
                _logger.Info($"EventSub chat subscription: broadcasterId={broadcasterId}, status=created, http=202");
            }
            catch (TwitchApiException ex)
            {
                keepSubscriptionKey = ex.StatusCode == System.Net.HttpStatusCode.Conflict;
                var status = ex.StatusCode == System.Net.HttpStatusCode.Conflict ? "already-exists" : "failed";
                _logger.Warn($"EventSub chat subscription: broadcasterId={broadcasterId}, status={status}, http={(int)ex.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.Warn($"EventSub chat subscription: broadcasterId={broadcasterId}, status=failed, http=none, error={ex.GetType().Name}");
            }
            finally
            {
                if (!keepSubscriptionKey || !IsActiveSession(context, sessionId))
                {
                    ForgetSubscriptionKey(context, key);
                }
            }

            return keepSubscriptionKey && IsActiveSession(context, sessionId);
        }
        finally
        {
            context.SubscriptionGate.Release();
        }
    }

    private async Task<bool> SubscribeRequestedChatMessagesAsync(
        RunContext context,
        string sessionId,
        IEnumerable<string> broadcasterIds,
        CancellationToken cancellationToken)
    {
        var primarySubscriptionRequested = false;
        var primarySubscriptionReady = false;
        foreach (var broadcasterId in broadcasterIds)
        {
            var subscriptionReady = await SubscribeChatMessageAsync(
                context,
                sessionId,
                broadcasterId,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(broadcasterId, context.BroadcasterId, StringComparison.Ordinal))
            {
                primarySubscriptionRequested = true;
                primarySubscriptionReady = subscriptionReady;
            }
        }

        return primarySubscriptionRequested && primarySubscriptionReady;
    }

    private async Task SubscribeRequestedSharedChatAsync(
        RunContext context,
        string sessionId,
        IEnumerable<string> broadcasterIds,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var broadcasterId in broadcasterIds)
            {
                await SubscribeSharedChatAsync(
                    context,
                    sessionId,
                    broadcasterId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SubscribeSharedChatAsync(
        RunContext context,
        string sessionId,
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return;
        }

        await context.SubscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActiveSession(context, sessionId))
            {
                return;
            }

            foreach (var type in new[]
                 {
                     "channel.shared_chat.begin",
                     "channel.shared_chat.update",
                     "channel.shared_chat.end"
                 })
            {
                var key = type + "\n" + broadcasterId;
                lock (context.SubscriptionSync)
                {
                    if (!context.CreatedSubscriptionKeys.Add(key))
                    {
                        continue;
                    }
                }

                var keepSubscriptionKey = false;
                try
                {
                    await _apiClient.CreateSharedChatSubscriptionAsync(
                        sessionId,
                        broadcasterId,
                        type,
                        cancellationToken).ConfigureAwait(false);
                    keepSubscriptionKey = true;
                    _logger.Info($"Shared Chat subscription created: type={type}, broadcaster_id={broadcasterId}");
                }
                catch (TwitchApiException ex)
                {
                    keepSubscriptionKey = ex.StatusCode == System.Net.HttpStatusCode.Conflict;
                    _logger.Warn($"Shared Chat subscription unavailable: type={type}, broadcaster_id={broadcasterId}, status={(int)ex.StatusCode}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.Warn(
                        $"Shared Chat subscription failed: type={type}, broadcaster_id={broadcasterId}, error={ex.GetType().Name}");
                }
                finally
                {
                    if (!keepSubscriptionKey || !IsActiveSession(context, sessionId))
                    {
                        ForgetSubscriptionKey(context, key);
                    }
                }
            }

            try
            {
                var activeSession = await _apiClient.GetSharedChatSessionAsync(
                    broadcasterId,
                    cancellationToken).ConfigureAwait(false);
                if (activeSession is not null && IsActiveSession(context, sessionId))
                {
                    SharedChatSessionChanged?.Invoke(this, new SharedChatSessionEventArgs(
                        broadcasterId,
                        activeSession.SessionId,
                        activeSession.HostBroadcasterId,
                        string.Empty,
                        string.Empty,
                        activeSession.ParticipantBroadcasterIds
                            .Select(id => new SharedChatParticipant(id, string.Empty, string.Empty))
                            .ToArray(),
                        true));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.Warn($"Shared Chat current session check failed: broadcaster_id={broadcasterId}, error={ex.GetType().Name}");
            }
        }
        finally
        {
            context.SubscriptionGate.Release();
        }
    }

    private async Task SubscribeChannelPointsAsync(
        RunContext context,
        string sessionId,
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        if (!_apiClient.HasChannelPointsScope || !IsCurrent(context))
        {
            return;
        }

        await context.SubscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActiveSession(context, sessionId))
            {
                return;
            }

            var detailsAvailable = true;
            foreach (var (type, version) in new[]
                     {
                         (CustomRedemptionSubscription, "1"),
                         (AutomaticRedemptionSubscription, "2")
                     })
            {
                var key = type + "\n" + broadcasterId;
                lock (context.SubscriptionSync)
                {
                    if (!context.CreatedSubscriptionKeys.Add(key))
                    {
                        continue;
                    }
                }

                var keepSubscriptionKey = false;
                try
                {
                    await _apiClient.CreateChannelPointsSubscriptionAsync(
                        sessionId,
                        broadcasterId,
                        type,
                        version,
                        cancellationToken).ConfigureAwait(false);
                    keepSubscriptionKey = true;
                    _logger.Info($"Channel Points subscription created: type={type}, broadcaster_id={broadcasterId}");
                }
                catch (TwitchApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    keepSubscriptionKey = true;
                    _logger.Info($"Channel Points subscription already exists: type={type}, broadcaster_id={broadcasterId}");
                }
                catch (TwitchApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    detailsAvailable = false;
                    _logger.Warn($"Channel Points unavailable: type={type}, broadcaster_id={broadcasterId}, status={(int)ex.StatusCode}");
                    if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                        Interlocked.Exchange(ref context.ChannelPointsAuthorizationReported, 1) == 0 &&
                        IsCurrent(context))
                    {
                        ChannelPointsAuthorizationRequired?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    detailsAvailable = false;
                    _logger.Warn($"Channel Points subscription skipped: type={type}, broadcaster_id={broadcasterId}, error={ex.GetType().Name}");
                }
                finally
                {
                    if (!keepSubscriptionKey || !IsActiveSession(context, sessionId))
                    {
                        ForgetSubscriptionKey(context, key);
                    }
                }
            }

            if (IsCurrent(context) && IsActiveSession(context, sessionId))
            {
                ChannelPointsCapabilityChanged?.Invoke(
                    this,
                    new ChannelPointsCapabilityEventArgs(broadcasterId, detailsAvailable));
            }
        }
        finally
        {
            context.SubscriptionGate.Release();
        }
    }

    private async Task SubscribeRequestedChannelPointsAsync(
        RunContext context,
        string sessionId,
        IEnumerable<string> broadcasterIds,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var broadcasterId in broadcasterIds)
            {
                await SubscribeChannelPointsAsync(context, sessionId, broadcasterId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Channel Points subscription batch stopped: {ex.GetType().Name}");
        }
    }

    private async Task SubscribeModerationAsync(
        RunContext context,
        string sessionId,
        string broadcasterId,
        string moderatorUserId,
        CancellationToken cancellationToken)
    {
        if (!IsCurrent(context))
        {
            return;
        }

        var subscriptions = new List<(string Type, string Version)>();
        if (_apiClient.HasChatModerationScope)
        {
            subscriptions.Add(("channel.chat.message_delete", "1"));
            subscriptions.Add(("channel.chat.clear_user_messages", "1"));
        }
        if (_apiClient.HasAutoModScope)
        {
            subscriptions.Add(("automod.message.hold", "2"));
            subscriptions.Add(("automod.message.update", "2"));
        }
        if (_apiClient.HasChannelModerateScope)
        {
            subscriptions.Add(("channel.ban", "1"));
            subscriptions.Add(("channel.unban", "1"));
        }
        if (_apiClient.HasUnbanRequestsScope)
        {
            subscriptions.Add(("channel.unban_request.create", "1"));
            subscriptions.Add(("channel.unban_request.resolve", "1"));
        }

        foreach (var (type, version) in subscriptions)
        {
            var key = $"{type}:{version}:{broadcasterId}:{moderatorUserId}";
            await context.SubscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsActiveSession(context, sessionId))
                {
                    return;
                }

                lock (context.SubscriptionSync)
                {
                    if (!context.CreatedSubscriptionKeys.Add(key))
                    {
                        continue;
                    }
                }

                var keepSubscriptionKey = false;
                try
                {
                    await _apiClient.CreateModerationSubscriptionAsync(
                        sessionId,
                        type,
                        version,
                        broadcasterId,
                        moderatorUserId,
                        cancellationToken).ConfigureAwait(false);
                    keepSubscriptionKey = true;
                    _logger.Info($"Moderation subscription created: type={type}, broadcaster_id={broadcasterId}");
                }
                catch (TwitchApiException ex)
                {
                    keepSubscriptionKey = ex.StatusCode == System.Net.HttpStatusCode.Conflict;
                    _logger.Warn($"Moderation subscription unavailable: type={type}, broadcaster_id={broadcasterId}, status={(int)ex.StatusCode}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.Warn($"Moderation subscription failed: type={type}, broadcaster_id={broadcasterId}, error={ex.GetType().Name}");
                }
                finally
                {
                    if (!keepSubscriptionKey || !IsActiveSession(context, sessionId))
                    {
                        ForgetSubscriptionKey(context, key);
                    }
                }
            }
            finally
            {
                context.SubscriptionGate.Release();
            }
        }
    }

    private async Task SubscribeRequestedModerationAsync(
        RunContext context,
        string sessionId,
        IEnumerable<string> broadcasterIds,
        string moderatorUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var broadcasterId in broadcasterIds)
            {
                await SubscribeModerationAsync(
                    context,
                    sessionId,
                    broadcasterId,
                    moderatorUserId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Moderation subscription batch stopped: {ex.GetType().Name}");
        }
    }

    private static UnbanRequestEntry? ParseUnbanRequest(JsonElement root, bool resolved)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("event", out var evt) ||
            evt.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var requestId = GetString(evt, "id");
        var broadcasterId = GetString(evt, "broadcaster_user_id");
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(broadcasterId))
        {
            return null;
        }

        var statusText = resolved ? GetString(evt, "status") : "pending";
        return new UnbanRequestEntry
        {
            RequestId = requestId,
            BroadcasterId = broadcasterId,
            UserId = GetString(evt, "user_id"),
            UserLogin = GetString(evt, "user_login"),
            DisplayName = GetString(evt, "user_name"),
            RequestText = GetString(evt, "text"),
            Status = Enum.TryParse<UnbanRequestStatus>(statusText, true, out var status) ? status : UnbanRequestStatus.Pending,
            CreatedAt = ParseTimestamp(GetString(evt, "created_at")),
            ResolvedAt = resolved ? ParseTimestamp(GetString(evt, "resolved_at")) : null,
            ResolutionText = GetString(evt, "resolution_text"),
            ModeratorId = GetString(evt, "moderator_user_id"),
            ModeratorName = GetString(evt, "moderator_user_name")
        };
    }

    private static ChatMessageModel? ParseChannelPointsRedemption(
        JsonElement root,
        JsonElement metadata,
        string subscriptionType)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("event", out var evt) ||
            evt.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var redemptionId = GetString(evt, "id");
        if (string.IsNullOrWhiteSpace(redemptionId))
        {
            return null;
        }

        var isAutomatic = string.Equals(subscriptionType, AutomaticRedemptionSubscription, StringComparison.Ordinal);
        evt.TryGetProperty("reward", out var reward);
        var rewardTitle = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "title") : string.Empty;
        var rewardType = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "type") : string.Empty;
        var rewardCost = reward.ValueKind == JsonValueKind.Object
            ? GetInt(reward, isAutomatic ? "channel_points" : "cost")
            : null;
        var userInput = GetString(evt, "user_input");
        var messageText = string.Empty;
        var parts = new ObservableCollection<ChatMessagePartModel>();

        if (isAutomatic && evt.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            messageText = GetString(message, "text");
            ParseMessageFragments(message, parts);
        }

        if (isAutomatic && parts.Count == 0 && reward.ValueKind == JsonValueKind.Object &&
            reward.TryGetProperty("emote", out var rewardEmote) && rewardEmote.ValueKind == JsonValueKind.Object)
        {
            var emoteId = GetString(rewardEmote, "id");
            var emoteName = GetString(rewardEmote, "name");
            if (!string.IsNullOrWhiteSpace(emoteId))
            {
                parts.Add(ChatMessagePartModel.TwitchEmote(emoteName, emoteId));
            }
        }

        var redeemedAt = ParseTimestamp(GetString(evt, "redeemed_at"));
        var messageId = GetString(evt, "message_id");
        return new ChatMessageModel
        {
            Id = redemptionId,
            MessageId = messageId,
            Kind = ChatMessageKind.ChannelPointsRedemption,
            RedemptionId = redemptionId,
            Timestamp = redeemedAt,
            RedeemedAt = redeemedAt,
            ChannelLogin = GetString(evt, "broadcaster_user_login"),
            BroadcasterId = GetString(evt, "broadcaster_user_id"),
            ChannelDisplayName = GetString(evt, "broadcaster_user_name"),
            RedemptionStatus = GetString(evt, "status"),
            UserId = GetString(evt, "user_id"),
            Login = GetString(evt, "user_login"),
            DisplayName = GetString(evt, "user_name"),
            Text = string.IsNullOrWhiteSpace(userInput) ? messageText : userInput,
            RewardId = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "id") : string.Empty,
            RewardTitle = rewardTitle,
            RewardCost = rewardCost,
            RewardPrompt = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "prompt") : string.Empty,
            RewardUserInput = userInput,
            RewardType = rewardType,
            Parts = parts
        };
    }

    private static void ParseMessageFragments(JsonElement message, ICollection<ChatMessagePartModel> parts)
    {
        if (!message.TryGetProperty("fragments", out var fragments) || fragments.ValueKind != JsonValueKind.Array)
        {
            var text = GetString(message, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(ChatMessagePartModel.TextPart(text));
            }
            return;
        }

        foreach (var fragment in fragments.EnumerateArray())
        {
            var text = GetString(fragment, "text");
            if (fragment.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object)
            {
                var emoteId = GetString(emote, "id");
                if (!string.IsNullOrWhiteSpace(emoteId))
                {
                    parts.Add(ChatMessagePartModel.TwitchEmote(text, emoteId));
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(ChatMessagePartModel.TextPart(text));
            }
        }
    }

    private static bool IsChannelPointsSubscription(string value) =>
        string.Equals(value, CustomRedemptionSubscription, StringComparison.Ordinal) ||
        string.Equals(value, AutomaticRedemptionSubscription, StringComparison.Ordinal);

    private static async Task ObserveCanceledReceiveAsync(Task<string?> receiveTask)
    {
        try
        {
            _ = await receiveTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task ObserveConnectionCompletionAsync(Task<ListenResult> connectionTask)
    {
        Task<ListenResult>? currentTask = connectionTask;
        while (currentTask is not null)
        {
            try
            {
                var result = await currentTask.ConfigureAwait(false);
                currentTask = result.HandoffTask;
            }
            catch
            {
                // The parent connection is already stopping and only needs to observe the child task.
                break;
            }
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    continue;
                }

                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaxEventPayloadBytes)
                {
                    throw new InvalidDataException("Twitch EventSub message exceeded the size limit.");
                }

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void RaiseStatus(
        RunContext context,
        ChannelConnectionState state,
        string error = "",
        string errorCode = "")
    {
        if (IsCurrent(context))
        {
            StatusChanged?.Invoke(this, new EventSubConnectionStatusEventArgs(state, error, errorCode));
        }
    }

    private static void TrackBackgroundTask(RunContext context, Task task)
    {
        lock (context.SubscriptionSync)
        {
            context.BackgroundTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (context.SubscriptionSync)
                {
                    context.BackgroundTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RaiseMessage(RunContext context, ChatMessageModel message)
    {
        if (IsCurrent(context))
        {
            MessageReceived?.Invoke(this, message);
        }
    }

    private void MarkConnected(RunContext context)
    {
        if (!IsCurrent(context))
        {
            return;
        }

        Interlocked.Exchange(ref context.ConnectedReported, 1);
        context.InitialConnection.TrySetResult(true);
        _logger.Info("Chat connected.");
        RaiseStatus(context, ChannelConnectionState.Connected);
    }

    private bool IsCurrent(RunContext context)
    {
        lock (_gate)
        {
            return ReferenceEquals(_currentRun, context);
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        var dot = value.IndexOf('.');
        var z = value.IndexOf('Z');
        if (dot >= 0 && z > dot + 8)
        {
            var trimmed = value[..(dot + 8)] + value[z..];
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private sealed record ListenResult(
        string? ReconnectUrl,
        bool NeedsSubscription,
        Task<ListenResult>? HandoffTask = null);

    private sealed class CoreChatSubscriptionRevokedException(string subscriptionType, string status)
        : InvalidOperationException("Twitch revoked the core chat EventSub subscription.")
    {
        public string SubscriptionType { get; } = subscriptionType;
        public string Status { get; } = status;
    }

    private sealed class RunContext
    {
        public RunContext(
            long generation,
            string broadcasterId,
            string chattingUserId,
            CancellationTokenSource cancellation)
        {
            Generation = generation;
            BroadcasterId = broadcasterId;
            ChattingUserId = chattingUserId;
            Cancellation = cancellation;
            RequestedChatBroadcasters.Add(broadcasterId);
            RequestedChannelPointsBroadcasters.Add(broadcasterId);
        }

        public long Generation { get; }
        public string BroadcasterId { get; }
        public string ChattingUserId { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<bool> InitialConnection { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunTask { get; set; } = Task.CompletedTask;
        public ClientWebSocket? Socket { get; set; }
        public string ActiveSessionId { get; set; } = string.Empty;
        public object SubscriptionSync { get; } = new();
        public HashSet<string> RequestedChannelPointsBroadcasters { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RequestedChatBroadcasters { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RequestedModerationBroadcasters { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CreatedSubscriptionKeys { get; } = new(StringComparer.Ordinal);
        public List<Task> BackgroundTasks { get; } = [];
        public SemaphoreSlim SubscriptionGate { get; } = new(1, 1);
        public int ConnectedReported;
        public int ChannelPointsAuthorizationReported;
        public int HandoffInProgress;
    }
}

public sealed class ChannelPointsCapabilityEventArgs(string broadcasterId, bool available) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public bool Available { get; } = available;
}
