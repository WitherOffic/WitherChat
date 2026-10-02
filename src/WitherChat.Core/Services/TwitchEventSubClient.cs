using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class AutoModHeldEventArgs(HeldAutoModMessage message) : EventArgs
{
    public HeldAutoModMessage Message { get; } = message;
}

public sealed class AutoModResolvedEventArgs(string broadcasterId, string messageId) : EventArgs
{
    public string BroadcasterId { get; } = broadcasterId;
    public string MessageId { get; } = messageId;
}

public sealed class EventSubBanEventArgs(EventSubBan value) : EventArgs
{
    public EventSubBan Value { get; } = value;
}

public sealed class EventSubUnbanEventArgs(EventSubUnban value) : EventArgs
{
    public EventSubUnban Value { get; } = value;
}

public sealed class EventSubUnbanRequestEventArgs(EventSubUnbanRequest value) : EventArgs
{
    public EventSubUnbanRequest Value { get; } = value;
}

public sealed class SharedChatStateEventArgs(SharedChatState value) : EventArgs
{
    public SharedChatState Value { get; } = value;
}

public sealed class EventSubMessageDeletedEventArgs(EventSubMessageDeleted value) : EventArgs
{
    public EventSubMessageDeleted Value { get; } = value;
}

public sealed class EventSubUserMessagesClearedEventArgs(EventSubUserMessagesCleared value) : EventArgs
{
    public EventSubUserMessagesCleared Value { get; } = value;
}

public sealed class EventSubChatClearedEventArgs(EventSubChatCleared value) : EventArgs
{
    public EventSubChatCleared Value { get; } = value;
}

public sealed class TwitchEventSubClient : IAsyncDisposable
{
    private const string DefaultWebSocketUrl = "wss://eventsub.wss.twitch.tv/ws";
    private const int MaximumMessageBytes = 1_048_576;
    private const int MaximumRememberedMessageIds = 2_048;
    internal static IReadOnlyList<string> UserChatSubscriptionTypes { get; } =
    [
        "channel.chat.message",
        "channel.chat.notification",
        "channel.chat.clear"
    ];
    private readonly TwitchChatApiClient _apiClient;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly object _messageIdLock = new();
    private readonly HashSet<string> _detailedChannelPointChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _eventSubChatChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rememberedMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _messageIdOrder = new();
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private int _disposed;

    public TwitchEventSubClient(TwitchChatApiClient apiClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    public event EventHandler<ChatMessageEventArgs>? MessageReceived;
    public event EventHandler<StreamEventEventArgs>? StreamEventReceived;
    public event EventHandler<AutoModHeldEventArgs>? AutoModMessageHeld;
    public event EventHandler<AutoModResolvedEventArgs>? AutoModMessageResolved;
    public event EventHandler<EventSubBanEventArgs>? UserBanned;
    public event EventHandler<EventSubUnbanEventArgs>? UserUnbanned;
    public event EventHandler<EventSubUnbanRequestEventArgs>? UnbanRequestChanged;
    public event EventHandler<SharedChatStateEventArgs>? SharedChatStateChanged;
    public event EventHandler<EventSubMessageDeletedEventArgs>? ChatMessageDeleted;
    public event EventHandler<EventSubUserMessagesClearedEventArgs>? UserMessagesCleared;
    public event EventHandler<EventSubChatClearedEventArgs>? ChatCleared;
    public event EventHandler? SubscriptionsReady;

    public bool HasDetailedChannelPoints(string channel)
    {
        lock (_stateLock)
        {
            return _detailedChannelPointChannels.Contains(channel);
        }
    }

    public bool HasEventSubChat(string channel)
    {
        lock (_stateLock)
        {
            return _eventSubChatChannels.Contains(channel);
        }
    }

    public async Task ConfigureAsync(
        TwitchAuthSession? session,
        IReadOnlyCollection<string> channels,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(channels);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            if (session is null || channels.Count == 0)
            {
                return;
            }

            var resolved = new List<EventSubChannel>(channels.Count);
            foreach (var channel in channels.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var profile = await _apiClient.GetUserProfileAsync(session, channel, cancellationToken)
                        .ConfigureAwait(false);
                    resolved.Add(new EventSubChannel(profile.Login, profile.Id));
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new HttpRequestException("Twitch channel lookup timed out.", exception);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or InvalidDataException or ArgumentException)
                {
                }
            }
            if (resolved.Count == 0)
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _runTask = RunAsync(session, resolved, cancellation.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        var task = Interlocked.Exchange(ref _runTask, null);
        lock (_stateLock)
        {
            _detailedChannelPointChannels.Clear();
            _eventSubChatChannels.Clear();
        }
        lock (_messageIdLock)
        {
            _rememberedMessageIds.Clear();
            _messageIdOrder.Clear();
        }
        if (cancellation is null)
        {
            return;
        }
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation.Dispose();
    }

    private Task RunAsync(
        TwitchAuthSession session,
        IReadOnlyList<EventSubChannel> channels,
        CancellationToken cancellationToken) =>
        RunReconnectLoopAsync(
            (endpoint, subscribe, token) => RunConnectionAsync(session, channels, endpoint, subscribe, token),
            new Uri(DefaultWebSocketUrl), TimeSpan.FromSeconds(2), cancellationToken);

    internal static async Task RunReconnectLoopAsync(
        Func<Uri, bool, CancellationToken, Task<Uri?>> connect,
        Uri defaultEndpoint,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var endpoint = defaultEndpoint;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var reconnectEndpoint = await connect(
                        endpoint,
                        endpoint == defaultEndpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (reconnectEndpoint is null)
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                endpoint = reconnectEndpoint ?? defaultEndpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or WebSocketException or HttpRequestException or
                    InvalidDataException or JsonException)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                endpoint = defaultEndpoint;
            }
        }
    }

    private async Task<Uri?> RunConnectionAsync(
        TwitchAuthSession session,
        IReadOnlyList<EventSubChannel> channels,
        Uri endpoint,
        bool createSubscriptions,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var receiveTimeout = TimeSpan.FromSeconds(15);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, receiveTimeout, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var messageType = GetNestedString(root, "metadata", "message_type");
            if (messageType == "session_welcome")
            {
                var sessionId = GetNestedString(root, "payload", "session", "id");
                var keepaliveSeconds = GetNestedInt(root, "payload", "session", "keepalive_timeout_seconds");
                if (keepaliveSeconds is > 0)
                {
                    receiveTimeout = TimeSpan.FromSeconds(Math.Clamp(keepaliveSeconds.Value, 5, 600) + 2);
                }
                if (sessionId.Length > 0)
                {
                    if (createSubscriptions)
                    {
                        await CreateSubscriptionsAsync(session, sessionId, channels, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    SubscriptionsReady?.Invoke(this, EventArgs.Empty);
                }
                continue;
            }
            if (messageType == "session_reconnect")
            {
                var reconnectUrl = GetNestedString(root, "payload", "session", "reconnect_url");
                return Uri.TryCreate(reconnectUrl, UriKind.Absolute, out var reconnectUri) &&
                       reconnectUri.Scheme == "wss"
                    ? reconnectUri
                    : null;
            }
            if (messageType == "notification")
            {
                var messageId = GetNestedString(root, "metadata", "message_id");
                if (RememberMessageId(messageId))
                {
                    HandleNotification(root);
                }
            }
        }
        return null;
    }

    private async Task CreateSubscriptionsAsync(
        TwitchAuthSession session,
        string webSocketSessionId,
        IReadOnlyList<EventSubChannel> channels,
        CancellationToken cancellationToken)
    {
        var scopes = new HashSet<string>(session.Scopes, StringComparer.OrdinalIgnoreCase);
        foreach (var channel in channels)
        {
            await TrySubscribeAsync(session, webSocketSessionId, "channel.shared_chat.begin", "1",
                BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);
            await TrySubscribeAsync(session, webSocketSessionId, "channel.shared_chat.update", "1",
                BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);
            await TrySubscribeAsync(session, webSocketSessionId, "channel.shared_chat.end", "1",
                BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);

            if (scopes.Contains("channel:read:redemptions"))
            {
                var custom = await TrySubscribeAsync(session, webSocketSessionId,
                    "channel.channel_points_custom_reward_redemption.add", "1",
                    BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);
                var automatic = await TrySubscribeAsync(session, webSocketSessionId,
                    "channel.channel_points_automatic_reward_redemption.add", "2",
                    BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);
                if (custom || automatic)
                {
                    lock (_stateLock)
                    {
                        _detailedChannelPointChannels.Add(channel.Login);
                    }
                }
            }

            if (scopes.Contains("user:read:chat"))
            {
                foreach (var subscriptionType in UserChatSubscriptionTypes)
                {
                    var subscribed = await TrySubscribeAsync(
                        session,
                        webSocketSessionId,
                        subscriptionType,
                        "1",
                        UserCondition(channel.Id, session.UserId),
                        cancellationToken).ConfigureAwait(false);
                    if (subscribed && string.Equals(
                            subscriptionType,
                            "channel.chat.message",
                            StringComparison.Ordinal))
                    {
                        lock (_stateLock)
                        {
                            _eventSubChatChannels.Add(channel.Login);
                        }
                    }
                }
            }

            if (scopes.Contains("moderator:manage:automod"))
            {
                await TrySubscribeAsync(session, webSocketSessionId, "automod.message.hold", "2",
                    ModeratorCondition(channel.Id, session.UserId), cancellationToken).ConfigureAwait(false);
                await TrySubscribeAsync(session, webSocketSessionId, "automod.message.update", "2",
                    ModeratorCondition(channel.Id, session.UserId), cancellationToken).ConfigureAwait(false);
            }
            if (scopes.Contains("channel:moderate"))
            {
                await TrySubscribeAsync(session, webSocketSessionId, "channel.ban", "1",
                    BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);
                await TrySubscribeAsync(session, webSocketSessionId, "channel.unban", "1",
                    BroadcasterCondition(channel.Id), cancellationToken).ConfigureAwait(false);
            }
            if (scopes.Contains("moderator:manage:unban_requests"))
            {
                await TrySubscribeAsync(session, webSocketSessionId, "channel.unban_request.create", "1",
                    ModeratorCondition(channel.Id, session.UserId), cancellationToken).ConfigureAwait(false);
                await TrySubscribeAsync(session, webSocketSessionId, "channel.unban_request.resolve", "1",
                    ModeratorCondition(channel.Id, session.UserId), cancellationToken).ConfigureAwait(false);
            }
            if (scopes.Contains("moderator:manage:chat_messages"))
            {
                await TrySubscribeAsync(session, webSocketSessionId, "channel.chat.message_delete", "1",
                    UserCondition(channel.Id, session.UserId), cancellationToken).ConfigureAwait(false);
                await TrySubscribeAsync(session, webSocketSessionId, "channel.chat.clear_user_messages", "1",
                    UserCondition(channel.Id, session.UserId), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TrySubscribeAsync(
        TwitchAuthSession session,
        string webSocketSessionId,
        string type,
        string version,
        IReadOnlyDictionary<string, string> condition,
        CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.CreateEventSubSubscriptionAsync(
                session, webSocketSessionId, type, version, condition, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return exception.StatusCode == HttpStatusCode.Conflict;
        }
    }

    internal void HandleNotification(JsonElement root)
    {
        var type = GetNestedString(root, "metadata", "subscription_type");
        if (!TryGetEvent(root, out var value))
        {
            return;
        }
        switch (type)
        {
            case "channel.channel_points_custom_reward_redemption.add":
            case "channel.channel_points_automatic_reward_redemption.add":
                if (ParseChannelPoints(value, type) is { } redemption)
                {
                    MessageReceived?.Invoke(this, new ChatMessageEventArgs(redemption));
                    StreamEventReceived?.Invoke(this, new StreamEventEventArgs(ToChannelPointsEvent(redemption)));
                }
                break;
            case "channel.chat.notification":
                if (ParseChatNotification(
                        value,
                        GetNestedString(root, "metadata", "message_id"),
                        GetNestedString(root, "metadata", "message_timestamp")) is { } notification)
                {
                    StreamEventReceived?.Invoke(this, new StreamEventEventArgs(notification));
                    MessageReceived?.Invoke(this, new ChatMessageEventArgs(ToSystemMessage(notification)));
                }
                break;
            case "automod.message.hold":
                if (ParseAutoMod(value, GetNestedString(root, "metadata", "message_timestamp")) is { } held)
                {
                    AutoModMessageHeld?.Invoke(this, new AutoModHeldEventArgs(held));
                }
                break;
            case "automod.message.update":
                AutoModMessageResolved?.Invoke(this, new AutoModResolvedEventArgs(
                    GetString(value, "broadcaster_user_id"), GetString(value, "message_id")));
                break;
            case "channel.ban":
                if (ParseBan(value) is { } ban)
                {
                    UserBanned?.Invoke(this, new EventSubBanEventArgs(ban));
                }
                break;
            case "channel.unban":
                UserUnbanned?.Invoke(this, new EventSubUnbanEventArgs(new EventSubUnban(
                    GetString(value, "broadcaster_user_id"), GetString(value, "broadcaster_user_login"),
                    GetString(value, "user_id"))));
                break;
            case "channel.unban_request.create":
            case "channel.unban_request.resolve":
                if (ParseUnbanRequest(value) is { } request)
                {
                    UnbanRequestChanged?.Invoke(this, new EventSubUnbanRequestEventArgs(request));
                }
                break;
            case "channel.shared_chat.begin":
            case "channel.shared_chat.update":
            case "channel.shared_chat.end":
                SharedChatStateChanged?.Invoke(this, new SharedChatStateEventArgs(ParseSharedChat(value, type)));
                break;
            case "channel.chat.message_delete":
                ChatMessageDeleted?.Invoke(this, new EventSubMessageDeletedEventArgs(new EventSubMessageDeleted(
                    GetString(value, "broadcaster_user_id"), GetString(value, "broadcaster_user_login"),
                    GetString(value, "message_id"), GetString(value, "target_user_id"))));
                break;
            case "channel.chat.clear_user_messages":
                UserMessagesCleared?.Invoke(this, new EventSubUserMessagesClearedEventArgs(new EventSubUserMessagesCleared(
                    GetString(value, "broadcaster_user_id"), GetString(value, "broadcaster_user_login"),
                    GetString(value, "target_user_id"))));
                break;
            case "channel.chat.clear":
                if (ParseChatClear(value, GetNestedString(root, "metadata", "message_timestamp")) is { } chatClear)
                {
                    ChatCleared?.Invoke(this, new EventSubChatClearedEventArgs(chatClear));
                }
                break;
            case "channel.chat.message":
                if (ParseChatMessage(value, GetNestedString(root, "metadata", "message_timestamp")) is { } chatMessage &&
                    !(chatMessage.IsChannelPointRedemption && HasDetailedChannelPoints(chatMessage.Channel)))
                {
                    if (ParseCheerEvent(value, chatMessage) is { } cheer)
                    {
                        chatMessage = chatMessage with { StreamEvent = cheer };
                        StreamEventReceived?.Invoke(this, new StreamEventEventArgs(cheer));
                    }
                    MessageReceived?.Invoke(this, new ChatMessageEventArgs(chatMessage));
                }
                break;
        }
    }

    internal static StreamEvent? ParseChatNotification(
        JsonElement value,
        string eventId,
        string messageTimestamp = "")
    {
        var noticeType = GetString(value, "notice_type");
        var kind = noticeType switch
        {
            "sub" or "shared_chat_sub" => StreamEventKinds.Subscription,
            "resub" or "shared_chat_resub" => StreamEventKinds.Resubscription,
            "sub_gift" or "shared_chat_sub_gift" => StreamEventKinds.GiftSubscription,
            "community_sub_gift" or "shared_chat_community_sub_gift" => StreamEventKinds.CommunityGift,
            "raid" or "shared_chat_raid" => StreamEventKinds.Raid,
            "announcement" or "shared_chat_announcement" => StreamEventKinds.Announcement,
            "charity_donation" => StreamEventKinds.Charity,
            _ => StreamEventKinds.Other
        };
        if (kind == StreamEventKinds.Other)
        {
            return null;
        }
        value.TryGetProperty("message", out var message);
        value.TryGetProperty(noticeType, out var detail);
        var count = detail.ValueKind == JsonValueKind.Object
            ? GetInt(detail, kind == StreamEventKinds.Raid ? "viewer_count" : "total") ?? 0
            : 0;
        var amountDisplay = string.Empty;
        long amountMicros = 0;
        var currency = string.Empty;
        if (kind == StreamEventKinds.Charity && detail.ValueKind == JsonValueKind.Object &&
            detail.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Object)
        {
            var valueAmount = GetInt(amount, "value") ?? 0;
            var decimalPlaces = Math.Clamp(GetInt(amount, "decimal_places") ?? 0, 0, 9);
            currency = GetString(amount, "currency");
            var divisor = (long)Math.Pow(10, decimalPlaces);
            amountMicros = divisor == 0 ? 0 : valueAmount * 1_000_000L / divisor;
            amountDisplay = divisor == 0
                ? valueAmount.ToString(CultureInfo.InvariantCulture)
                : ((decimal)valueAmount / divisor).ToString("0.##", CultureInfo.InvariantCulture) + " " + currency;
        }
        return new StreamEvent
        {
            Id = string.IsNullOrWhiteSpace(eventId) ? Guid.NewGuid().ToString("N") : eventId,
            Platform = ChatPlatforms.Twitch,
            Channel = GetString(value, "broadcaster_user_login"),
            Kind = kind,
            UserId = GetString(value, "chatter_user_id"),
            UserLogin = GetString(value, "chatter_user_login"),
            DisplayName = GetString(value, "chatter_user_name"),
            Message = GetString(message, "text"),
            AmountDisplay = amountDisplay,
            AmountMicros = amountMicros,
            Currency = currency,
            Count = count,
            Detail = noticeType,
            Timestamp = ParseTimestamp(messageTimestamp)
        };
    }

    private static StreamEvent? ParseCheerEvent(JsonElement value, ChatMessage message)
    {
        if (!value.TryGetProperty("cheer", out var cheer) || cheer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var bits = GetInt(cheer, "bits") ?? 0;
        if (bits <= 0)
        {
            return null;
        }
        return new StreamEvent
        {
            Id = "cheer-" + message.Id,
            Platform = ChatPlatforms.Twitch,
            Channel = message.Channel,
            Kind = StreamEventKinds.Cheer,
            UserId = message.UserId,
            UserLogin = message.UserLogin,
            DisplayName = message.DisplayName,
            Message = message.Text,
            AmountDisplay = bits.ToString("N0", CultureInfo.InvariantCulture) + " Bits",
            AmountMicros = bits * 10_000L,
            Count = bits,
            Timestamp = message.Timestamp
        };
    }

    private static StreamEvent ToChannelPointsEvent(ChatMessage message) => new()
    {
        Id = "reward-" + message.Id,
        Platform = ChatPlatforms.Twitch,
        Channel = message.Channel,
        Kind = StreamEventKinds.ChannelPoints,
        UserId = message.UserId,
        UserLogin = message.UserLogin,
        DisplayName = message.DisplayName,
        Message = message.Text,
        Count = message.RewardCost ?? 0,
        Detail = message.RewardTitle,
        Timestamp = message.Timestamp
    };

    public static ChatMessage ToSystemMessage(StreamEvent value) => new()
    {
        Id = "event-" + value.Id,
        PlatformMessageId = value.Id,
        Channel = value.Channel,
        UserId = value.UserId,
        UserLogin = value.UserLogin,
        DisplayName = value.DisplayName,
        Text = value.Message,
        Timestamp = value.Timestamp,
        Platform = value.Platform,
        Parts = value.Message.Length == 0 ? [] : [ChatMessagePart.PlainText(value.Message)],
        StreamEvent = value
    };

    internal static EventSubChatCleared? ParseChatClear(JsonElement value, string messageTimestamp = "")
    {
        var broadcasterLogin = GetString(value, "broadcaster_user_login").Trim().TrimStart('#');
        if (broadcasterLogin.Length == 0)
        {
            return null;
        }

        return new EventSubChatCleared(
            GetString(value, "broadcaster_user_id"),
            broadcasterLogin,
            ParseTimestamp(messageTimestamp));
    }

    internal static ChatMessage? ParseChatMessage(JsonElement value, string messageTimestamp = "")
    {
        var id = GetString(value, "message_id");
        if (id.Length == 0)
        {
            return null;
        }

        var sourceBroadcasterId = GetString(value, "source_broadcaster_user_id");
        var badgeProperty = sourceBroadcasterId.Length == 0 ? "badges" : "source_badges";
        var badges = new List<ChatBadge>();
        var hasBadges = value.TryGetProperty(badgeProperty, out var badgeArray) &&
                        badgeArray.ValueKind == JsonValueKind.Array;
        if (!hasBadges && badgeProperty != "badges")
        {
            hasBadges = value.TryGetProperty("badges", out badgeArray) &&
                        badgeArray.ValueKind == JsonValueKind.Array;
        }
        if (hasBadges)
        {
            foreach (var badge in badgeArray.EnumerateArray())
            {
                var setId = GetString(badge, "set_id");
                var versionId = GetString(badge, "id");
                if (setId.Length > 0)
                {
                    badges.Add(new ChatBadge(setId, versionId));
                }
            }
        }

        value.TryGetProperty("message", out var message);
        var text = GetString(message, "text");
        var parts = new List<ChatMessagePart>();
        if (message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("fragments", out var fragments) && fragments.ValueKind == JsonValueKind.Array)
        {
            foreach (var fragment in fragments.EnumerateArray())
            {
                var fragmentText = GetString(fragment, "text");
                if (string.Equals(GetString(fragment, "type"), "emote", StringComparison.OrdinalIgnoreCase) &&
                    fragment.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object)
                {
                    var isAnimated = emote.TryGetProperty("format", out var formats) &&
                                     formats.ValueKind == JsonValueKind.Array &&
                                     formats.EnumerateArray().Any(format =>
                                         format.ValueKind == JsonValueKind.String &&
                                         string.Equals(
                                             format.GetString(),
                                             "animated",
                                             StringComparison.OrdinalIgnoreCase));
                    parts.Add(ChatMessagePart.TwitchEmote(
                        fragmentText,
                        GetString(emote, "id"),
                        isAnimated));
                }
                else if (fragmentText.Length > 0)
                {
                    parts.Add(ChatMessagePart.PlainText(fragmentText));
                }
            }
        }
        if (parts.Count == 0 && text.Length > 0)
        {
            parts.Add(ChatMessagePart.PlainText(text));
        }

        value.TryGetProperty("reply", out var reply);
        return new ChatMessage
        {
            Id = id,
            Channel = GetString(value, "broadcaster_user_login"),
            BroadcasterId = GetString(value, "broadcaster_user_id"),
            UserId = GetString(value, "chatter_user_id"),
            UserLogin = GetString(value, "chatter_user_login"),
            DisplayName = GetString(value, "chatter_user_name"),
            Text = text,
            Timestamp = ParseTimestamp(messageTimestamp),
            UserColor = GetString(value, "color"),
            Badges = badges,
            Parts = parts,
            ReplyParentDisplayName = GetString(reply, "parent_user_name"),
            ReplyParentText = GetString(reply, "parent_message_body"),
            IsAction = string.Equals(GetString(value, "message_type"), "action", StringComparison.OrdinalIgnoreCase),
            CustomRewardId = GetString(value, "channel_points_custom_reward_id"),
            SourceBroadcasterId = sourceBroadcasterId,
            SourceChannelLogin = GetString(value, "source_broadcaster_user_login"),
            SourceChannelDisplayName = GetString(value, "source_broadcaster_user_name")
        };
    }

    internal static ChatMessage? ParseChannelPoints(JsonElement value, string subscriptionType)
    {
        var id = GetString(value, "id");
        if (id.Length == 0)
        {
            return null;
        }
        value.TryGetProperty("reward", out var reward);
        var isAutomatic = subscriptionType.Contains("automatic_reward", StringComparison.Ordinal);
        var rewardTitle = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "title") : string.Empty;
        var rewardCost = reward.ValueKind == JsonValueKind.Object
            ? GetInt(reward, isAutomatic ? "channel_points" : "cost")
            : null;
        var text = GetString(value, "user_input");
        if (text.Length == 0 && value.TryGetProperty("message", out var message))
        {
            text = GetString(message, "text");
        }
        return new ChatMessage
        {
            Id = id,
            Channel = GetString(value, "broadcaster_user_login"),
            BroadcasterId = GetString(value, "broadcaster_user_id"),
            UserId = GetString(value, "user_id"),
            UserLogin = GetString(value, "user_login"),
            DisplayName = GetString(value, "user_name"),
            Text = text,
            Timestamp = ParseTimestamp(GetString(value, "redeemed_at")),
            CustomRewardId = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "id") : id,
            RewardTitle = rewardTitle,
            RewardCost = rewardCost,
            RewardPrompt = reward.ValueKind == JsonValueKind.Object ? GetString(reward, "prompt") : string.Empty,
            Parts = text.Length == 0 ? [] : [ChatMessagePart.PlainText(text)]
        };
    }

    internal static HeldAutoModMessage? ParseAutoMod(JsonElement value, string messageTimestamp = "")
    {
        var id = GetString(value, "message_id");
        if (id.Length == 0)
        {
            return null;
        }
        value.TryGetProperty("message", out var message);
        value.TryGetProperty("reason", out var reason);
        var category = reason.ValueKind == JsonValueKind.Object ? GetString(reason, "category") : string.Empty;
        var level = reason.ValueKind == JsonValueKind.Object ? GetInt(reason, "level") ?? 0 : 0;
        if (reason.ValueKind == JsonValueKind.Object && reason.TryGetProperty("automod", out var autoMod) &&
            autoMod.ValueKind == JsonValueKind.Object)
        {
            category = category.Length == 0 ? GetString(autoMod, "category") : category;
            level = level == 0 ? GetInt(autoMod, "level") ?? 0 : level;
        }
        return new HeldAutoModMessage(
            id,
            GetString(value, "broadcaster_user_id"),
            GetString(value, "broadcaster_user_login"),
            GetString(value, "user_id"),
            GetString(value, "user_login"),
            GetString(value, "user_name"),
            GetString(message, "text"),
            category,
            level,
            ParseTimestamp(messageTimestamp.Length > 0 ? messageTimestamp : GetString(value, "held_at")));
    }

    private static EventSubBan? ParseBan(JsonElement value)
    {
        var userId = GetString(value, "user_id");
        if (userId.Length == 0)
        {
            return null;
        }
        var endsAtText = GetString(value, "ends_at");
        var endsAt = DateTimeOffset.TryParse(endsAtText, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsedEnd) ? parsedEnd : (DateTimeOffset?)null;
        return new EventSubBan(
            GetString(value, "broadcaster_user_id"), GetString(value, "broadcaster_user_login"),
            userId, GetString(value, "user_login"),
            GetString(value, "user_name"), GetString(value, "reason"),
            ParseTimestamp(GetString(value, "banned_at")), endsAt,
            value.TryGetProperty("is_permanent", out var permanent) && permanent.ValueKind == JsonValueKind.True);
    }

    private static EventSubUnbanRequest? ParseUnbanRequest(JsonElement value)
    {
        var id = GetString(value, "id");
        if (id.Length == 0)
        {
            return null;
        }
        return new EventSubUnbanRequest(
            id, GetString(value, "broadcaster_user_id"), GetString(value, "broadcaster_user_login"),
            GetString(value, "user_id"),
            GetString(value, "user_login"), GetString(value, "user_name"), GetString(value, "text"),
            ParseTimestamp(GetString(value, "created_at")), GetString(value, "status"));
    }

    private static SharedChatState ParseSharedChat(JsonElement value, string type)
    {
        var participantCount = 0;
        if (value.TryGetProperty("participants", out var participants) && participants.ValueKind == JsonValueKind.Array)
        {
            participantCount = participants.GetArrayLength();
        }
        return new SharedChatState(
            GetString(value, "broadcaster_user_id"), GetString(value, "broadcaster_user_login"),
            !type.EndsWith(".end", StringComparison.Ordinal),
            GetString(value, "session_id"), participantCount);
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveCancellation.CancelAfter(timeout);
        using var stream = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), receiveCancellation.Token)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("Twitch EventSub closed the connection.");
                }
                await stream.WriteAsync(buffer.AsMemory(0, result.Count), receiveCancellation.Token)
                    .ConfigureAwait(false);
                if (stream.Length > MaximumMessageBytes)
                {
                    throw new InvalidDataException("Twitch EventSub message exceeded the size limit.");
                }
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebSocketException("Twitch EventSub keepalive timed out.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal bool RememberMessageId(string messageId)
    {
        if (messageId.Length == 0)
        {
            return true;
        }

        lock (_messageIdLock)
        {
            if (!_rememberedMessageIds.Add(messageId))
            {
                return false;
            }

            _messageIdOrder.Enqueue(messageId);
            while (_messageIdOrder.Count > MaximumRememberedMessageIds)
            {
                _rememberedMessageIds.Remove(_messageIdOrder.Dequeue());
            }
            return true;
        }
    }

    private static IReadOnlyDictionary<string, string> BroadcasterCondition(string broadcasterId) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["broadcaster_user_id"] = broadcasterId };

    private static IReadOnlyDictionary<string, string> ModeratorCondition(string broadcasterId, string moderatorId) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broadcaster_user_id"] = broadcasterId,
            ["moderator_user_id"] = moderatorId
        };

    private static IReadOnlyDictionary<string, string> UserCondition(string broadcasterId, string userId) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broadcaster_user_id"] = broadcasterId,
            ["user_id"] = userId
        };

    private static bool TryGetEvent(JsonElement root, out JsonElement value)
    {
        value = default;
        return root.TryGetProperty("payload", out var payload) &&
               payload.TryGetProperty("event", out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static string GetNestedString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return string.Empty;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? string.Empty : string.Empty;
    }

    private static int? GetNestedInt(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }
        return current.TryGetInt32(out var value) ? value : null;
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed) ? parsed : null;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private sealed record EventSubChannel(string Login, string Id);
}
