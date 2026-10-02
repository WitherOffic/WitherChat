using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class YouTubeLiveChatClient : IAsyncDisposable
{
    private const string BroadcastsEndpoint = "https://www.googleapis.com/youtube/v3/liveBroadcasts";
    private const string MessagesEndpoint = "https://www.googleapis.com/youtube/v3/liveChat/messages";
    private const string BansEndpoint = "https://www.googleapis.com/youtube/v3/liveChat/bans";
    private static readonly TimeSpan RefreshSafetyWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BroadcastRetryDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollRetryInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollRetryMaximumDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthorizationRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AuthorizationRetryMaximumDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QuotaRetryDelay = TimeSpan.FromMinutes(5);
    private const int PollFailuresBeforeReconnectStatus = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly YouTubeAuthService _authService;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenMessageOrder = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private YouTubeAuthSession? _session;
    private bool _disposed;

    public YouTubeLiveChatClient(YouTubeAuthService authService, HttpMessageHandler? handler = null)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(35);
    }

    public event EventHandler<ChatMessageEventArgs>? MessageReceived;
    public event EventHandler<StreamEventEventArgs>? StreamEventReceived;
    public event EventHandler<YouTubeMessageDeletedEventArgs>? MessageDeleted;
    public event EventHandler<ChatConnectionStatusEventArgs>? StatusChanged;
    public event EventHandler<YouTubeSessionEventArgs>? SessionUpdated;

    public bool IsRunning => _runTask is not null;
    public bool IsConnected { get; private set; }
    public string CurrentChannel { get; private set; } = string.Empty;
    public string BroadcastTitle { get; private set; } = string.Empty;
    public string CurrentLiveChatId { get; private set; } = string.Empty;

    public async Task StartAsync(YouTubeAuthSession session, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync().ConfigureAwait(false);
            _session = session;
            CurrentLiveChatId = string.Empty;
            _seenMessageIds.Clear();
            _seenMessageOrder.Clear();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAsync(_runCancellation.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The YouTube transport loop must report transient API failures and continue reconnecting.")]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var authorizationRetryDelay = AuthorizationRetryDelayOverride ?? AuthorizationRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var session = await EnsureValidSessionAsync(cancellationToken).ConfigureAwait(false);
                CurrentChannel = GetChannelKey(session);
                SetStatus(ChatConnectionState.Connecting, CurrentChannel, "Searching for an active YouTube broadcast.");
                var broadcast = await LoadActiveBroadcastAsync(session, cancellationToken).ConfigureAwait(false);
                // A successful authenticated API call proves that a previous 401 loop
                // has recovered, so the next authorization failure may retry quickly.
                authorizationRetryDelay = AuthorizationRetryDelayOverride ?? AuthorizationRetryDelay;
                if (broadcast is null || string.IsNullOrWhiteSpace(broadcast.Snippet.LiveChatId))
                {
                    IsConnected = false;
                    BroadcastTitle = string.Empty;
                    CurrentLiveChatId = string.Empty;
                    SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, "No active YouTube broadcast was found.");
                    await Task.Delay(BroadcastRetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                BroadcastTitle = broadcast.Snippet.Title;
                CurrentLiveChatId = broadcast.Snippet.LiveChatId;
                IsConnected = true;
                SetStatus(ChatConnectionState.Connected, CurrentChannel, BroadcastTitle);
                await PollMessagesAsync(broadcast.Snippet.LiveChatId, cancellationToken).ConfigureAwait(false);
                IsConnected = false;
                SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, "The active YouTube chat ended.");
                await Task.Delay(BroadcastRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (YouTubeApiException exception) when (
                IsQuotaError(exception.Reason))
            {
                // Quota exhaustion can also happen while discovering the active
                // broadcast, before PollMessagesAsync owns the retry loop.
                IsConnected = false;
                SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, exception.Message);
                await Task.Delay(
                    QuotaRetryDelayOverride ?? QuotaRetryDelay,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (YouTubeApiException exception) when (
                exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Access tokens can be revoked or invalidated before their local expiry.
                // Refresh immediately instead of leaving the live chat in a permanent
                // 20-second reconnect loop with the same rejected token.
                IsConnected = false;
                SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, exception.Message);
                var refreshSucceeded = false;
                try
                {
                    await ForceRefreshSessionAsync(cancellationToken).ConfigureAwait(false);
                    refreshSucceeded = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception refreshException)
                {
                    SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, refreshException.Message);
                }

                var retryDelay = refreshSucceeded ? authorizationRetryDelay : BroadcastRetryDelay;
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                if (refreshSucceeded)
                {
                    authorizationRetryDelay = AdvanceAuthorizationRetryDelay(authorizationRetryDelay);
                }
            }
            catch (Exception exception)
            {
                IsConnected = false;
                SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, exception.Message);
                await Task.Delay(BroadcastRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        IsConnected = false;
        BroadcastTitle = string.Empty;
        CurrentLiveChatId = string.Empty;
        SetStatus(ChatConnectionState.Disconnected, CurrentChannel);
    }

    public async Task DeleteMessageAsync(
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsYouTubeMessage)
        {
            throw new ArgumentException("The message does not belong to YouTube.", nameof(message));
        }
        var messageId = string.IsNullOrWhiteSpace(message.PlatformMessageId)
            ? message.Id
            : message.PlatformMessageId;
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("The YouTube message ID is empty.", nameof(message));
        }

        var session = await EnsureModerationSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            MessagesEndpoint + "?id=" + Uri.EscapeDataString(messageId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<YouTubeChatBan> BanUserAsync(
        ChatMessage message,
        int? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsYouTubeMessage || string.IsNullOrWhiteSpace(message.UserId))
        {
            throw new ArgumentException("The YouTube user channel ID is empty.", nameof(message));
        }
        if (durationSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        var liveChatId = CurrentLiveChatId;
        if (!IsConnected || string.IsNullOrWhiteSpace(liveChatId))
        {
            throw new InvalidOperationException("The active YouTube live chat is not connected.");
        }

        var session = await EnsureModerationSessionAsync(cancellationToken).ConfigureAwait(false);
        var snippet = new Dictionary<string, object?>
        {
            ["liveChatId"] = liveChatId,
            ["type"] = durationSeconds is null ? "permanent" : "temporary",
            ["bannedUserDetails"] = new Dictionary<string, string>
            {
                ["channelId"] = message.UserId
            }
        };
        if (durationSeconds is { } seconds)
        {
            snippet["banDurationSeconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BansEndpoint + "?part=snippet")
        {
            Content = JsonContent.Create(new Dictionary<string, object?> { ["snippet"] = snippet })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var resource = await JsonSerializer.DeserializeAsync<LiveChatBanResource>(
                           stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("YouTube returned an empty ban response.");
        if (string.IsNullOrWhiteSpace(resource.Id))
        {
            throw new InvalidDataException("YouTube returned an empty ban ID.");
        }
        var createdAt = DateTimeOffset.UtcNow;
        return new YouTubeChatBan(
            resource.Id,
            liveChatId,
            message.UserId,
            message.UserLabel,
            createdAt,
            durationSeconds is { } value ? createdAt.AddSeconds(value) : null);
    }

    public async Task RemoveBanAsync(string banId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(banId);
        var session = await EnsureModerationSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            BansEndpoint + "?id=" + Uri.EscapeDataString(banId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task PollMessagesAsync(string liveChatId, CancellationToken cancellationToken)
    {
        string? pageToken = null;
        var retryDelay = PollRetryDelayOverride ?? PollRetryInitialDelay;
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var session = await EnsureValidSessionAsync(cancellationToken).ConfigureAwait(false);
            var uri = MessagesEndpoint + "?part=id%2Csnippet%2CauthorDetails" +
                      "&maxResults=200&profileImageSize=88&liveChatId=" + Uri.EscapeDataString(liveChatId) +
                      (string.IsNullOrWhiteSpace(pageToken) ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            LiveChatMessageListResponse payload;
            try
            {
                payload = await GetAsync<LiveChatMessageListResponse>(
                        session.AccessToken,
                        uri,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (YouTubeApiException exception) when (IsTerminalLiveChatError(exception.Reason))
            {
                return;
            }
            catch (YouTubeApiException exception) when (
                IsQuotaError(exception.Reason))
            {
                // A daily quota exhaustion is not a short network outage. Retrying it
                // every few seconds only consumes resources and makes the status flap.
                // Keep the current chat/page token and probe at a deliberately slow rate.
                consecutiveFailures++;
                if (IsConnected)
                {
                    IsConnected = false;
                    SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, exception.Message);
                }
                await Task.Delay(
                    QuotaRetryDelayOverride ?? QuotaRetryDelay,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested && IsTransientPollException(exception))
            {
                consecutiveFailures++;
                if (consecutiveFailures >= PollFailuresBeforeReconnectStatus && IsConnected)
                {
                    IsConnected = false;
                    SetStatus(ChatConnectionState.Reconnecting, CurrentChannel, exception.Message);
                }
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    PollRetryMaximumDelay.TotalMilliseconds,
                    retryDelay.TotalMilliseconds * 2));
                continue;
            }

            consecutiveFailures = 0;
            retryDelay = PollRetryDelayOverride ?? PollRetryInitialDelay;
            if (!IsConnected)
            {
                IsConnected = true;
                SetStatus(ChatConnectionState.Connected, CurrentChannel, BroadcastTitle);
            }
            foreach (var item in payload.Items ?? [])
            {
                if (string.Equals(item.Snippet.Type, "tombstone", StringComparison.OrdinalIgnoreCase))
                {
                    MessageDeleted?.Invoke(this, new YouTubeMessageDeletedEventArgs(item.Id));
                    continue;
                }
                if (!RememberMessage(CreateMessageDedupeKey(item)))
                {
                    continue;
                }
                var streamEvent = ConvertStreamEvent(item, session);
                if (streamEvent is not null)
                {
                    StreamEventReceived?.Invoke(this, new StreamEventEventArgs(streamEvent));
                    MessageReceived?.Invoke(
                        this,
                        new ChatMessageEventArgs(TwitchEventSubClient.ToSystemMessage(streamEvent) with
                        {
                            BroadcasterId = session.ChannelId,
                            UserProfileImageUri = TryCreateHttpUri(item.AuthorDetails?.ProfileImageUrl)
                        }));
                    continue;
                }
                var message = ConvertMessage(item, session);
                if (message is not null)
                {
                    MessageReceived?.Invoke(this, new ChatMessageEventArgs(message));
                }
            }
            pageToken = payload.NextPageToken;
            if (payload.OfflineAt is not null)
            {
                return;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(payload.PollingIntervalMillis, 1_000, 30_000));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<YouTubeAuthSession> EnsureValidSessionAsync(CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session ?? throw new InvalidOperationException("YouTube is not signed in.");
            if (!session.NeedsRefresh(RefreshSafetyWindow))
            {
                return session;
            }
            session = await _authService.RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            _session = session;
            SessionUpdated?.Invoke(this, new YouTubeSessionEventArgs(session));
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<YouTubeAuthSession> ForceRefreshSessionAsync(CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session ?? throw new InvalidOperationException("YouTube is not signed in.");
            session = await _authService.RefreshAsync(session, cancellationToken).ConfigureAwait(false);
            _session = session;
            SessionUpdated?.Invoke(this, new YouTubeSessionEventArgs(session));
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<YouTubeAuthSession> EnsureModerationSessionAsync(CancellationToken cancellationToken)
    {
        var session = await EnsureValidSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!YouTubeAuthService.HasModerationScope(session))
        {
            throw new InvalidOperationException("Reconnect YouTube and grant chat moderation permission.");
        }
        return session;
    }

    private async Task<LiveBroadcastResource?> LoadActiveBroadcastAsync(
        YouTubeAuthSession session,
        CancellationToken cancellationToken)
    {
        var uri = BroadcastsEndpoint +
                  "?part=id%2Csnippet%2Cstatus&broadcastStatus=active&broadcastType=all&maxResults=5";
        var payload = await GetAsync<LiveBroadcastListResponse>(session.AccessToken, uri, cancellationToken)
            .ConfigureAwait(false);
        return payload.Items?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Snippet.LiveChatId));
    }

    private async Task<T> GetAsync<T>(string accessToken, string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            GoogleErrorResponse? error = null;
            try
            {
                error = JsonSerializer.Deserialize<GoogleErrorResponse>(body, JsonOptions);
            }
            catch (JsonException)
            {
            }
            throw CreateApiException(error, response.StatusCode, response.ReasonPhrase);
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("YouTube returned an empty JSON response.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        GoogleErrorResponse? error = null;
        try
        {
            error = JsonSerializer.Deserialize<GoogleErrorResponse>(body, JsonOptions);
        }
        catch (JsonException)
        {
        }
        var statusCode = response.StatusCode;
        var exception = CreateApiException(error, statusCode, response.ReasonPhrase);
        response.Dispose();
        throw exception;
    }

    private static YouTubeApiException CreateApiException(
        GoogleErrorResponse? response,
        System.Net.HttpStatusCode statusCode,
        string? reasonPhrase)
    {
        var reason = response?.Error?.Errors?.FirstOrDefault()?.Reason ?? string.Empty;
        var message = response?.Error?.Message ?? reasonPhrase ?? "Unknown YouTube error";
        return new YouTubeApiException("YouTube request failed: " + message, statusCode, reason);
    }

    private static bool IsTerminalLiveChatError(string reason) =>
        reason is "liveChatEnded" or "liveChatNotFound" or "liveChatDisabled";

    private static bool IsQuotaError(string reason) =>
        reason is "quotaExceeded" or "dailyLimitExceeded";

    internal static TimeSpan AdvanceAuthorizationRetryDelay(TimeSpan currentDelay)
    {
        if (currentDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(currentDelay));
        }
        return TimeSpan.FromMilliseconds(Math.Min(
            AuthorizationRetryMaximumDelay.TotalMilliseconds,
            currentDelay.TotalMilliseconds * 2));
    }

    private static bool IsTransientPollException(Exception exception)
    {
        if (exception is YouTubeApiException apiException)
        {
            var code = (int?)apiException.StatusCode;
            return code is 408 or 429 or >= 500 ||
                   apiException.Reason is "rateLimitExceeded" or "userRequestsExceedRateLimit" or
                       "backendError" or "internalError";
        }
        return exception is HttpRequestException or IOException or InvalidDataException or JsonException or
            TaskCanceledException;
    }

    internal static ChatMessage? ConvertMessage(LiveChatMessageResource item, YouTubeAuthSession session)
    {
        if (item.AuthorDetails is null || item.Snippet is null)
        {
            return null;
        }
        var text = item.Snippet.TextMessageDetails?.MessageText;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = item.Snippet.DisplayMessage;
        }
        if (string.IsNullOrWhiteSpace(text) && item.Snippet.GiftEventDetails?.GiftMetadata is { } gift)
        {
            text = string.IsNullOrWhiteSpace(gift.AltText) ? gift.GiftName : gift.AltText;
            if (gift.ComboCount > 1)
            {
                text += " ×" + gift.ComboCount;
            }
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var badges = new List<ChatBadge>
        {
            new("youtube", "1", Title: "YouTube")
        };
        if (item.AuthorDetails.IsChatOwner)
        {
            badges.Add(new ChatBadge("broadcaster", "1", Title: "witherchat.youtube.owner"));
        }
        else if (item.AuthorDetails.IsChatModerator)
        {
            badges.Add(new ChatBadge("moderator", "1", Title: "witherchat.youtube.moderator"));
        }
        if (item.AuthorDetails.IsChatSponsor)
        {
            badges.Add(new ChatBadge("subscriber", "1", Title: "witherchat.youtube.sponsor"));
        }

        return new ChatMessage
        {
            Id = CreateMessageDedupeKey(item),
            PlatformMessageId = item.Id,
            Channel = GetChannelKey(session),
            BroadcasterId = session.ChannelId,
            UserId = item.AuthorDetails.ChannelId,
            UserLogin = item.AuthorDetails.ChannelId,
            DisplayName = item.AuthorDetails.DisplayName,
            Text = text,
            Timestamp = item.Snippet.PublishedAt,
            UserColor = CreateUserColor(item.AuthorDetails.ChannelId),
            Badges = badges,
            Parts = [ChatMessagePart.PlainText(text)],
            Platform = ChatPlatforms.YouTube,
            UserProfileImageUri = TryCreateHttpUri(item.AuthorDetails.ProfileImageUrl)
        };
    }

    internal static StreamEvent? ConvertStreamEvent(LiveChatMessageResource item, YouTubeAuthSession session)
    {
        if (item.Snippet is null || item.AuthorDetails is null)
        {
            return null;
        }
        var kind = item.Snippet.Type switch
        {
            "superChatEvent" => StreamEventKinds.SuperChat,
            "superStickerEvent" => StreamEventKinds.SuperSticker,
            "newSponsorEvent" or "memberMilestoneChatEvent" => StreamEventKinds.Membership,
            "membershipGiftingEvent" => StreamEventKinds.MembershipGift,
            "giftMembershipReceivedEvent" => StreamEventKinds.GiftMembershipReceived,
            "pollEvent" => StreamEventKinds.Poll,
            "giftEvent" => StreamEventKinds.Donation,
            _ => string.Empty
        };
        if (kind.Length == 0)
        {
            return null;
        }
        var message = item.Snippet.DisplayMessage;
        var amountDisplay = string.Empty;
        long amountMicros = 0;
        var currency = string.Empty;
        var count = 0;
        var detail = string.Empty;
        Uri? imageUri = null;
        if (item.Snippet.SuperChatDetails is { } superChat)
        {
            amountDisplay = superChat.AmountDisplayString;
            amountMicros = superChat.AmountMicros;
            currency = superChat.Currency;
            message = string.IsNullOrWhiteSpace(superChat.UserComment) ? message : superChat.UserComment;
            detail = "tier:" + superChat.Tier;
        }
        else if (item.Snippet.SuperStickerDetails is { } superSticker)
        {
            amountDisplay = superSticker.AmountDisplayString;
            amountMicros = superSticker.AmountMicros;
            currency = superSticker.Currency;
            message = superSticker.SuperStickerMetadata?.AltText ?? message;
            detail = "tier:" + superSticker.Tier;
        }
        else if (item.Snippet.MembershipGiftingDetails is { } membershipGift)
        {
            count = membershipGift.GiftMembershipsCount;
            detail = membershipGift.GiftMembershipsLevelName;
        }
        else if (item.Snippet.GiftMembershipReceivedDetails is { } receivedGift)
        {
            detail = receivedGift.MemberLevelName;
        }
        else if (item.Snippet.NewSponsorDetails is { } sponsor)
        {
            detail = sponsor.MemberLevelName;
        }
        else if (item.Snippet.MemberMilestoneChatDetails is { } milestone)
        {
            count = milestone.MemberMonth;
            detail = milestone.MemberLevelName;
            message = string.IsNullOrWhiteSpace(milestone.UserComment) ? message : milestone.UserComment;
        }
        else if (item.Snippet.PollDetails?.Metadata is { } poll)
        {
            message = poll.QuestionText;
            detail = poll.Status;
            count = poll.Options?.Length ?? 0;
        }
        else if (item.Snippet.GiftEventDetails?.GiftMetadata is { } gift)
        {
            count = gift.ComboCount;
            message = string.IsNullOrWhiteSpace(gift.AltText) ? gift.GiftName : gift.AltText;
            amountDisplay = gift.JewelsAmount > 0 ? gift.JewelsAmount + " Jewels" : string.Empty;
            detail = gift.GiftName;
            imageUri = TryCreateHttpUri(gift.GiftUrl);
        }
        return new StreamEvent
        {
            Id = CreateMessageDedupeKey(item),
            Platform = ChatPlatforms.YouTube,
            Channel = GetChannelKey(session),
            Kind = kind,
            UserId = item.AuthorDetails.ChannelId,
            UserLogin = item.AuthorDetails.ChannelId,
            DisplayName = item.AuthorDetails.DisplayName,
            Message = message ?? string.Empty,
            AmountDisplay = amountDisplay ?? string.Empty,
            AmountMicros = amountMicros,
            Currency = currency ?? string.Empty,
            Count = count,
            Detail = detail ?? string.Empty,
            ImageUri = imageUri,
            Timestamp = item.Snippet.PublishedAt
        };
    }

    private bool RememberMessage(string id)
    {
        if (!_seenMessageIds.Add(id))
        {
            return false;
        }
        _seenMessageOrder.Enqueue(id);
        while (_seenMessageOrder.Count > 5_000)
        {
            _seenMessageIds.Remove(_seenMessageOrder.Dequeue());
        }
        return true;
    }

    internal static string CreateMessageDedupeKey(LiveChatMessageResource item)
    {
        var comboCount = item.Snippet.GiftEventDetails?.GiftMetadata?.ComboCount;
        return string.Equals(item.Snippet.Type, "giftEvent", StringComparison.OrdinalIgnoreCase) &&
               comboCount is not null
            ? item.Id + ":gift:" + comboCount.Value
            : item.Id;
    }

    private void SetStatus(ChatConnectionState state, string channel, string? detail = null) =>
        StatusChanged?.Invoke(this, new ChatConnectionStatusEventArgs(state, channel, detail));

    private async Task StopCoreAsync()
    {
        var cancellation = _runCancellation;
        var task = _runTask;
        _runCancellation = null;
        _runTask = null;
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _lifecycleLock.Dispose();
        _sessionLock.Dispose();
    }

    private static string GetChannelKey(YouTubeAuthSession session) => "youtube_" + session.ChannelId;

    private static Uri? TryCreateHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    private static string CreateUserColor(string value)
    {
        uint hash = 2166136261;
        foreach (var character in value ?? string.Empty)
        {
            hash = unchecked((hash ^ character) * 16777619);
        }
        var hue = (int)(hash % 360);
        var c = 0.62;
        var x = c * (1 - Math.Abs(hue / 60d % 2 - 1));
        var m = 0.78 - c / 2;
        var (red, green, blue) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return $"#{(int)((red + m) * 255):X2}{(int)((green + m) * 255):X2}{(int)((blue + m) * 255):X2}";
    }

    internal TimeSpan? PollRetryDelayOverride { get; set; }

    internal TimeSpan? AuthorizationRetryDelayOverride { get; set; }

    internal TimeSpan? QuotaRetryDelayOverride { get; set; }

    internal sealed record LiveChatMessageListResponse(
        [property: JsonPropertyName("nextPageToken")] string? NextPageToken,
        [property: JsonPropertyName("pollingIntervalMillis")] int PollingIntervalMillis,
        [property: JsonPropertyName("offlineAt")] DateTimeOffset? OfflineAt,
        [property: JsonPropertyName("items")] LiveChatMessageResource[]? Items);
    internal sealed record LiveChatMessageResource(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("snippet")] LiveChatMessageSnippet Snippet,
        [property: JsonPropertyName("authorDetails")] LiveChatAuthorDetails AuthorDetails);
    internal sealed record LiveChatMessageSnippet(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
        [property: JsonPropertyName("displayMessage")] string DisplayMessage,
        [property: JsonPropertyName("textMessageDetails")] TextMessageDetails? TextMessageDetails,
        [property: JsonPropertyName("giftEventDetails")] GiftEventDetails? GiftEventDetails = null,
        [property: JsonPropertyName("superChatDetails")] SuperChatDetails? SuperChatDetails = null,
        [property: JsonPropertyName("superStickerDetails")] SuperStickerDetails? SuperStickerDetails = null,
        [property: JsonPropertyName("newSponsorDetails")] NewSponsorDetails? NewSponsorDetails = null,
        [property: JsonPropertyName("memberMilestoneChatDetails")] MemberMilestoneChatDetails? MemberMilestoneChatDetails = null,
        [property: JsonPropertyName("membershipGiftingDetails")] MembershipGiftingDetails? MembershipGiftingDetails = null,
        [property: JsonPropertyName("giftMembershipReceivedDetails")] GiftMembershipReceivedDetails? GiftMembershipReceivedDetails = null,
        [property: JsonPropertyName("pollDetails")] PollDetails? PollDetails = null);
    internal sealed record TextMessageDetails([property: JsonPropertyName("messageText")] string MessageText);
    internal sealed record GiftEventDetails(
        [property: JsonPropertyName("giftMetadata")] GiftMetadata? GiftMetadata);
    internal sealed record GiftMetadata(
        [property: JsonPropertyName("giftName")] string GiftName,
        [property: JsonPropertyName("comboCount")] int ComboCount,
        [property: JsonPropertyName("altText")] string AltText,
        [property: JsonPropertyName("jewelsAmount")] int JewelsAmount = 0,
        [property: JsonPropertyName("giftUrl")] string GiftUrl = "");
    internal sealed record SuperChatDetails(
        [property: JsonPropertyName("amountMicros")] long AmountMicros,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("amountDisplayString")] string AmountDisplayString,
        [property: JsonPropertyName("userComment")] string UserComment,
        [property: JsonPropertyName("tier")] int Tier);
    internal sealed record SuperStickerDetails(
        [property: JsonPropertyName("superStickerMetadata")] SuperStickerMetadata? SuperStickerMetadata,
        [property: JsonPropertyName("amountMicros")] long AmountMicros,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("amountDisplayString")] string AmountDisplayString,
        [property: JsonPropertyName("tier")] int Tier);
    internal sealed record SuperStickerMetadata(
        [property: JsonPropertyName("stickerId")] string StickerId,
        [property: JsonPropertyName("altText")] string AltText,
        [property: JsonPropertyName("language")] string Language);
    internal sealed record NewSponsorDetails(
        [property: JsonPropertyName("memberLevelName")] string MemberLevelName,
        [property: JsonPropertyName("isUpgrade")] bool IsUpgrade);
    internal sealed record MemberMilestoneChatDetails(
        [property: JsonPropertyName("userComment")] string UserComment,
        [property: JsonPropertyName("memberMonth")] int MemberMonth,
        [property: JsonPropertyName("memberLevelName")] string MemberLevelName);
    internal sealed record MembershipGiftingDetails(
        [property: JsonPropertyName("giftMembershipsCount")] int GiftMembershipsCount,
        [property: JsonPropertyName("giftMembershipsLevelName")] string GiftMembershipsLevelName);
    internal sealed record GiftMembershipReceivedDetails(
        [property: JsonPropertyName("memberLevelName")] string MemberLevelName,
        [property: JsonPropertyName("gifterChannelId")] string GifterChannelId,
        [property: JsonPropertyName("associatedMembershipGiftingMessageId")] string AssociatedMembershipGiftingMessageId);
    internal sealed record PollDetails([property: JsonPropertyName("metadata")] PollMetadata? Metadata);
    internal sealed record PollMetadata(
        [property: JsonPropertyName("questionText")] string QuestionText,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("options")] PollOption[]? Options);
    internal sealed record PollOption(
        [property: JsonPropertyName("optionText")] string OptionText,
        [property: JsonPropertyName("tally")] string Tally);
    internal sealed record LiveChatAuthorDetails(
        [property: JsonPropertyName("channelId")] string ChannelId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("profileImageUrl")] string ProfileImageUrl,
        [property: JsonPropertyName("isChatOwner")] bool IsChatOwner,
        [property: JsonPropertyName("isChatModerator")] bool IsChatModerator,
        [property: JsonPropertyName("isChatSponsor")] bool IsChatSponsor);
    private sealed record LiveBroadcastListResponse(
        [property: JsonPropertyName("items")] LiveBroadcastResource[]? Items);
    private sealed record LiveBroadcastResource(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("snippet")] LiveBroadcastSnippet Snippet);
    private sealed record LiveBroadcastSnippet(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("liveChatId")] string? LiveChatId);
    private sealed record GoogleErrorResponse([property: JsonPropertyName("error")] GoogleError? Error);
    private sealed record GoogleError(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("errors")] GoogleErrorDetail[]? Errors);
    private sealed record GoogleErrorDetail([property: JsonPropertyName("reason")] string Reason);
    private sealed class YouTubeApiException(
        string message,
        System.Net.HttpStatusCode statusCode,
        string reason) : HttpRequestException(message, null, statusCode)
    {
        public string Reason { get; } = reason;
    }
    private sealed record LiveChatBanResource([property: JsonPropertyName("id")] string Id);
}

public sealed class YouTubeSessionEventArgs(YouTubeAuthSession session) : EventArgs
{
    public YouTubeAuthSession Session { get; } = session;
}

public sealed class YouTubeMessageDeletedEventArgs(string messageId) : EventArgs
{
    public string MessageId { get; } = messageId;
}
