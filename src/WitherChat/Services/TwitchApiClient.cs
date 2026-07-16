using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class TwitchApiClient : IChannelSearchService, IDisposable
{
    private static readonly TimeSpan ChannelSearchCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<string> _clientIdProvider;
    private readonly AuthService _authService;
    private readonly FileLogger _logger;
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true
    })
    {
        BaseAddress = new Uri("https://api.twitch.tv/helix/"),
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly Dictionary<string, (DateTimeOffset CreatedAt, IReadOnlyList<ChannelSearchResult> Results)> _channelSearchCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _channelSearchCacheGate = new();

    public TwitchApiClient(Func<string> clientIdProvider, AuthService authService, FileLogger logger)
    {
        _clientIdProvider = clientIdProvider;
        _authService = authService;
        _logger = logger;
    }

    public void Dispose() => _http.Dispose();

    public bool IsOnlineSearchAvailable => _authService.HasAccessToken;
    public bool HasChannelPointsScope => _authService.HasScope(AuthService.ChannelPointsScope);
    public bool HasAutoModScope => _authService.HasScope(AuthService.AutoModScope);
    public bool HasChatModerationScope => _authService.HasScope(AuthService.ChatModerationScope);
    public bool HasBannedUsersScope => _authService.HasScope(AuthService.BannedUsersScope);
    public bool HasUnbanRequestsScope => _authService.HasScope(AuthService.UnbanRequestsScope);
    public bool HasChannelModerateScope => _authService.HasScope(AuthService.ChannelModerateScope);

    public async Task<IReadOnlyList<ChannelSearchResult>> SearchChannelsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim().TrimStart('@');
        if (normalizedQuery.Length < 2)
        {
            return [];
        }

        lock (_channelSearchCacheGate)
        {
            foreach (var expired in _channelSearchCache
                         .Where(entry => DateTimeOffset.UtcNow - entry.Value.CreatedAt >= ChannelSearchCacheDuration)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _channelSearchCache.Remove(expired);
            }

            if (_channelSearchCache.TryGetValue(normalizedQuery, out var cached) &&
                DateTimeOffset.UtcNow - cached.CreatedAt < ChannelSearchCacheDuration)
            {
                return cached.Results;
            }
        }

        var url = $"search/channels?query={Uri.EscapeDataString(normalizedQuery)}&live_only=false&first=6";
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<ChannelSearchDto>>(response, cancellationToken).ConfigureAwait(false);
        var results = payload.Data
            .Select(item => item.ToModel())
            .OrderBy(item => !string.Equals(item.BroadcasterLogin, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();

        lock (_channelSearchCacheGate)
        {
            _channelSearchCache[normalizedQuery] = (DateTimeOffset.UtcNow, results);
        }

        return results;
    }

    public async Task<TwitchUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, "users"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<UserDto>>(response, cancellationToken).ConfigureAwait(false);
        var user = payload.Data.FirstOrDefault() ?? throw new InvalidOperationException(L("TwitchEmptyCurrentUser"));
        return user.ToModel();
    }

    public async Task<TwitchUser?> GetUserByLoginAsync(string login, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return null;
        }

        var url = "users?login=" + Uri.EscapeDataString(login.Trim().TrimStart('@').ToLowerInvariant());
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<UserDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.FirstOrDefault()?.ToModel();
    }

    public async Task<TwitchUser?> GetUserByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var url = "users?id=" + Uri.EscapeDataString(id.Trim());
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<UserDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.FirstOrDefault()?.ToModel();
    }

    public async Task<PinnedChatMessageModel?> GetPinnedChatMessageAsync(
        string broadcasterId,
        string moderatorId,
        CancellationToken cancellationToken = default)
    {
        var url = $"chat/pins?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(moderatorId)}";
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Get Pinned Chat Message", HttpMethod.Get.Method).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<PinnedChatMessageDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.FirstOrDefault()?.ToModel();
    }

    public async Task<ChannelModerationAccess> GetModerationAccessAsync(
        string broadcasterId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(broadcasterId, userId, StringComparison.Ordinal))
        {
            return new ChannelModerationAccess(true, false, true);
        }

        if (!_authService.HasScope(AuthService.ModeratedChannelsScope))
        {
            return new ChannelModerationAccess(false, false, false, "missing_scope");
        }

        try
        {
            var cursor = string.Empty;
            do
            {
                var url = "moderation/channels?user_id=" + Uri.EscapeDataString(userId) + "&first=100";
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    url += "&after=" + Uri.EscapeDataString(cursor);
                }

                using var response = await SendAuthorizedAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, url),
                    cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                var payload = await ReadJsonAsync<HelixData<ModeratedChannelDto>>(response, cancellationToken).ConfigureAwait(false);
                if (payload.Data.Any(channel => string.Equals(channel.BroadcasterId, broadcasterId, StringComparison.Ordinal)))
                {
                    return new ChannelModerationAccess(false, true, true);
                }

                cursor = payload.Pagination.Cursor;
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            return new ChannelModerationAccess(false, false, false);
        }
        catch (TwitchApiException ex)
        {
            _logger.Warn($"Moderation access check failed: status={(int)ex.StatusCode}");
            return new ChannelModerationAccess(false, false, false, "http_" + (int)ex.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Moderation access check failed: {ex.GetType().Name}");
            return new ChannelModerationAccess(false, false, false, "network");
        }
    }

    public async Task<IReadOnlyList<TwitchBadgeDefinition>> GetGlobalChatBadgesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, "chat/badges/global"), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<BadgeSetDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.SelectMany(set => set.ToDefinitions()).ToList();
    }

    public async Task<IReadOnlyList<TwitchBadgeDefinition>> GetChannelChatBadgesAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return [];
        }

        var url = "chat/badges?broadcaster_id=" + Uri.EscapeDataString(broadcasterId.Trim());
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<BadgeSetDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.SelectMany(set => set.ToDefinitions()).ToList();
    }

    public async Task<StreamStatusInfo> GetStreamStatusAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return new StreamStatusInfo(false, 0, string.Empty);
        }

        var url = "streams?user_id=" + Uri.EscapeDataString(broadcasterId.Trim());
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<StreamDto>>(response, cancellationToken).ConfigureAwait(false);
        var stream = payload.Data.FirstOrDefault();
        return stream is null
            ? new StreamStatusInfo(false, 0, string.Empty)
            : new StreamStatusInfo(true, stream.ViewerCount, stream.Title, stream.GameName, stream.StartedAt);
    }

    public async Task CreateChatMessageSubscriptionAsync(
        string sessionId,
        string broadcasterUserId,
        string chattingUserId,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            type = "channel.chat.message",
            version = "1",
            condition = new
            {
                broadcaster_user_id = broadcasterUserId,
                user_id = chattingUserId
            },
            transport = new
            {
                method = "websocket",
                session_id = sessionId
            }
        };

        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, "eventsub/subscriptions", body),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            var exception = await TwitchApiException.FromResponseAsync(response).ConfigureAwait(false);
            _logger.Error($"Create channel.chat.message subscription failed: {(int)exception.StatusCode} {exception.ResponseBody}");
            throw exception;
        }
    }

    public async Task CreateSharedChatSubscriptionAsync(
        string sessionId,
        string broadcasterUserId,
        string subscriptionType,
        CancellationToken cancellationToken = default)
    {
        if (subscriptionType is not ("channel.shared_chat.begin" or
            "channel.shared_chat.update" or
            "channel.shared_chat.end"))
        {
            throw new ArgumentOutOfRangeException(nameof(subscriptionType));
        }

        var body = new
        {
            type = subscriptionType,
            version = "1",
            condition = new { broadcaster_user_id = broadcasterUserId },
            transport = new { method = "websocket", session_id = sessionId }
        };

        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, "eventsub/subscriptions", body),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw await TwitchApiException.FromResponseAsync(response).ConfigureAwait(false);
        }
    }

    public async Task<SharedChatSessionInfo?> GetSharedChatSessionAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        var url = "shared_chat/session?broadcaster_id=" + Uri.EscapeDataString(broadcasterId.Trim());
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Get Shared Chat Session", HttpMethod.Get.Method).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<SharedChatSessionDto>>(response, cancellationToken).ConfigureAwait(false);
        var session = payload.Data.FirstOrDefault();
        return session is null
            ? null
            : new SharedChatSessionInfo(
                session.SessionId,
                session.HostBroadcasterId,
                session.Participants.Select(participant => participant.BroadcasterId).ToArray());
    }

    public async Task CreateChannelPointsSubscriptionAsync(
        string sessionId,
        string broadcasterUserId,
        string subscriptionType,
        string version,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            type = subscriptionType,
            version,
            condition = new { broadcaster_user_id = broadcasterUserId },
            transport = new { method = "websocket", session_id = sessionId }
        };

        var accessToken = await _authService.EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var response = await SendOnceAsync(
            () => CreateJsonRequest(HttpMethod.Post, "eventsub/subscriptions", body),
            accessToken,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw await TwitchApiException.FromResponseAsync(response).ConfigureAwait(false);
        }
    }

    public async Task<SendChatMessageResult> SendChatMessageAsync(
        string broadcasterId,
        string senderId,
        string message,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            broadcaster_id = broadcasterId,
            sender_id = senderId,
            message
        };

        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, "chat/messages", body),
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<SendChatMessageDto>>(response, cancellationToken).ConfigureAwait(false);
        var data = payload.Data.FirstOrDefault();
        if (data is null)
        {
            return new SendChatMessageResult
            {
                IsSent = false,
                DropCode = "empty_response",
                DropMessage = L("TwitchEmptySendResponse")
            };
        }

        return new SendChatMessageResult
        {
            MessageId = string.IsNullOrWhiteSpace(data.MessageId) ? Guid.NewGuid().ToString("N") : data.MessageId,
            IsSent = data.IsSent,
            DropCode = data.DropReason?.Code,
            DropMessage = data.DropReason?.Message
        };
    }

    public async Task BanOrTimeoutUserAsync(
        string broadcasterId,
        string moderatorId,
        string targetUserId,
        string reason,
        int? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var url = $"moderation/bans?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(moderatorId)}";
        var body = new
        {
            data = new
            {
                user_id = targetUserId,
                duration = durationSeconds,
                reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            }
        };

        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, url, body),
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task DeleteChatMessageAsync(string broadcasterId, string moderatorId, string messageId, CancellationToken cancellationToken = default)
    {
        var url = $"moderation/chat?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(moderatorId)}&message_id={Uri.EscapeDataString(messageId)}";
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task UnbanUserAsync(string broadcasterId, string moderatorId, string targetUserId, CancellationToken cancellationToken = default)
    {
        var url = $"moderation/bans?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(moderatorId)}&user_id={Uri.EscapeDataString(targetUserId)}";
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task CreateModerationSubscriptionAsync(
        string sessionId,
        string subscriptionType,
        string version,
        string broadcasterUserId,
        string moderatorUserId,
        CancellationToken cancellationToken = default)
    {
        object condition = subscriptionType is "channel.ban" or "channel.unban"
            ? new { broadcaster_user_id = broadcasterUserId }
            : subscriptionType.StartsWith("automod.", StringComparison.Ordinal) ||
              subscriptionType.StartsWith("channel.unban_request.", StringComparison.Ordinal)
            ? new { broadcaster_user_id = broadcasterUserId, moderator_user_id = moderatorUserId }
            : new { broadcaster_user_id = broadcasterUserId, user_id = moderatorUserId };
        var body = new
        {
            type = subscriptionType,
            version,
            condition,
            transport = new { method = "websocket", session_id = sessionId }
        };
        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, "eventsub/subscriptions", body),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw await TwitchApiException.FromResponseAsync(response).ConfigureAwait(false);
        }
    }

    public async Task ManageHeldAutoModMessageAsync(
        string moderatorUserId,
        string messageId,
        bool allow,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            user_id = moderatorUserId,
            msg_id = messageId,
            action = allow ? "ALLOW" : "DENY"
        };
        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, "moderation/automod/message", body),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response).ConfigureAwait(false);
    }

    public async Task<BannedUsersPage> GetBannedUsersAsync(
        string broadcasterId,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var url = "moderation/banned?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) + "&first=100";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += "&after=" + Uri.EscapeDataString(cursor);
        }
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Get Banned Users", HttpMethod.Get.Method).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<BannedUserDto>>(response, cancellationToken).ConfigureAwait(false);
        _logger.Info($"Get Banned Users completed: status={(int)response.StatusCode}, broadcaster_id={broadcasterId}, elapsed_ms={stopwatch.ElapsedMilliseconds}");
        return new BannedUsersPage(payload.Data.Select(item => item.ToModel()).ToArray(), payload.Pagination.Cursor);
    }

    public async Task<UnbanRequestsPage> GetUnbanRequestsAsync(
        string broadcasterId,
        string moderatorId,
        UnbanRequestStatus status,
        int first = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var statusText = status.ToString().ToLowerInvariant();
        var url = $"moderation/unban_requests?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(moderatorId)}&status={statusText}&first={Math.Clamp(first, 1, 100)}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            url += "&after=" + Uri.EscapeDataString(cursor);
        }

        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Get Unban Requests", HttpMethod.Get.Method).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<UnbanRequestDto>>(response, cancellationToken).ConfigureAwait(false);
        return new UnbanRequestsPage(payload.Data.Select(item => item.ToModel()).ToArray(), payload.Pagination.Cursor);
    }

    public async Task<UnbanRequestEntry> ResolveUnbanRequestAsync(
        string broadcasterId,
        string moderatorId,
        string requestId,
        UnbanRequestStatus status,
        string resolutionText,
        CancellationToken cancellationToken = default)
    {
        if (status is not (UnbanRequestStatus.Approved or UnbanRequestStatus.Denied))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        resolutionText = (resolutionText ?? string.Empty).Trim();
        if (resolutionText.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(resolutionText));
        }

        var url = $"moderation/unban_requests?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&moderator_id={Uri.EscapeDataString(moderatorId)}&unban_request_id={Uri.EscapeDataString(requestId)}&status={status.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(resolutionText))
        {
            url += "&resolution_text=" + Uri.EscapeDataString(resolutionText);
        }
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, url),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Resolve Unban Request", HttpMethod.Patch.Method).ConfigureAwait(false);
        var payload = await ReadJsonAsync<HelixData<UnbanRequestDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.FirstOrDefault()?.ToModel() ?? throw new InvalidOperationException(L("TwitchEmptyUnbanResponse"));
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        var accessToken = await _authService.EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(requestFactory, accessToken, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        try
        {
            if (await _authService.ValidateCurrentAccessTokenAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Twitch API returned 401 for this request, but the OAuth token is still valid. Preserving the signed-in session.");
                return response;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Twitch API returned 401 and token validation could not be completed ({ex.GetType().Name}). Preserving the signed-in session.");
            return response;
        }

        response.Dispose();
        _logger.Warn("Twitch API returned 401 and OAuth validation confirmed that the token is invalid. Asking user to sign in again.");
        await _authService.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        accessToken = await _authService.EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(requestFactory, accessToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> requestFactory, string accessToken, CancellationToken cancellationToken)
    {
        using var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Client-Id", GetClientIdOrThrow());
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private string GetClientIdOrThrow()
    {
        var clientId = _clientIdProvider().Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(L("TwitchClientIdRequired"));
        }

        return clientId;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string url, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string endpointName = "Twitch API", string? httpMethod = null)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await TwitchApiException.FromResponseAsync(response, endpointName, httpMethod).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException(L("TwitchEmptyJson"));
    }

    private static string L(string key) =>
        LocalizationService.Get(LocalizationService.CurrentLanguage, key);

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;

    private sealed class HelixData<T>
    {
        [JsonPropertyName("data")]
        public List<T> Data { get; set; } = [];

        [JsonPropertyName("pagination")]
        public PaginationDto Pagination { get; set; } = new();
    }

    private sealed class PaginationDto
    {
        [JsonPropertyName("cursor")]
        public string Cursor { get; set; } = string.Empty;
    }

    private sealed class SharedChatSessionDto
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("host_broadcaster_id")]
        public string HostBroadcasterId { get; set; } = string.Empty;

        [JsonPropertyName("participants")]
        public List<SharedChatParticipantDto> Participants { get; set; } = [];
    }

    private sealed class SharedChatParticipantDto
    {
        [JsonPropertyName("broadcaster_id")]
        public string BroadcasterId { get; set; } = string.Empty;
    }

    private sealed class ModeratedChannelDto
    {
        [JsonPropertyName("broadcaster_id")]
        public string BroadcasterId { get; set; } = string.Empty;
    }

    private sealed class PinnedChatMessageDto
    {
        [JsonPropertyName("message_id")] public string MessageId { get; set; } = string.Empty;
        [JsonPropertyName("broadcaster_id")] public string BroadcasterId { get; set; } = string.Empty;
        [JsonPropertyName("sender_user_id")] public string SenderUserId { get; set; } = string.Empty;
        [JsonPropertyName("sender_user_login")] public string SenderUserLogin { get; set; } = string.Empty;
        [JsonPropertyName("sender_user_name")] public string SenderUserName { get; set; } = string.Empty;
        [JsonPropertyName("pinned_by_user_id")] public string PinnedByUserId { get; set; } = string.Empty;
        [JsonPropertyName("pinned_by_user_login")] public string PinnedByUserLogin { get; set; } = string.Empty;
        [JsonPropertyName("pinned_by_user_name")] public string PinnedByUserName { get; set; } = string.Empty;
        [JsonPropertyName("message")] public PinnedChatMessageBodyDto Message { get; set; } = new();
        [JsonPropertyName("starts_at")] public string StartsAt { get; set; } = string.Empty;
        [JsonPropertyName("ends_at")] public string? EndsAt { get; set; }
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = string.Empty;

        public PinnedChatMessageModel ToModel() => new()
        {
            MessageId = MessageId,
            BroadcasterId = BroadcasterId,
            SenderUserId = SenderUserId,
            SenderUserLogin = SenderUserLogin,
            SenderDisplayName = SenderUserName,
            PinnedByUserId = PinnedByUserId,
            PinnedByUserLogin = PinnedByUserLogin,
            PinnedByDisplayName = PinnedByUserName,
            Text = Message.Text,
            StartsAt = ParseTimestamp(StartsAt) ?? DateTimeOffset.UtcNow,
            EndsAt = ParseTimestamp(EndsAt),
            UpdatedAt = ParseTimestamp(UpdatedAt) ?? DateTimeOffset.UtcNow
        };
    }

    private sealed class PinnedChatMessageBodyDto
    {
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    }

    private sealed class BannedUserDto
    {
        [JsonPropertyName("user_id")]
        public string UserId { get; set; } = string.Empty;

        [JsonPropertyName("user_login")]
        public string UserLogin { get; set; } = string.Empty;

        [JsonPropertyName("user_name")]
        public string UserName { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public string ExpiresAt { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        public BannedUserEntry ToModel() => new()
        {
            UserId = UserId,
            UserLogin = UserLogin,
            DisplayName = UserName,
            CreatedAt = ParseTimestamp(CreatedAt) ?? DateTimeOffset.UtcNow,
            ExpiresAt = ParseTimestamp(ExpiresAt),
            Reason = Reason
        };
    }

    private sealed class UnbanRequestDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("broadcaster_id")] public string BroadcasterId { get; set; } = string.Empty;
        [JsonPropertyName("moderator_id")] public string ModeratorId { get; set; } = string.Empty;
        [JsonPropertyName("moderator_name")] public string ModeratorName { get; set; } = string.Empty;
        [JsonPropertyName("user_id")] public string UserId { get; set; } = string.Empty;
        [JsonPropertyName("user_login")] public string UserLogin { get; set; } = string.Empty;
        [JsonPropertyName("user_name")] public string UserName { get; set; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = string.Empty;
        [JsonPropertyName("resolved_at")] public string ResolvedAt { get; set; } = string.Empty;
        [JsonPropertyName("resolution_text")] public string ResolutionText { get; set; } = string.Empty;

        public UnbanRequestEntry ToModel() => new()
        {
            RequestId = Id,
            BroadcasterId = BroadcasterId,
            UserId = UserId,
            UserLogin = UserLogin,
            DisplayName = UserName,
            RequestText = Text,
            Status = Enum.TryParse<UnbanRequestStatus>(Status, true, out var status) ? status : UnbanRequestStatus.Pending,
            CreatedAt = ParseTimestamp(CreatedAt) ?? DateTimeOffset.UtcNow,
            ResolvedAt = ParseTimestamp(ResolvedAt),
            ResolutionText = ResolutionText,
            ModeratorId = ModeratorId,
            ModeratorName = ModeratorName
        };
    }

    private sealed class UserDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("profile_image_url")]
        public string ProfileImageUrl { get; set; } = string.Empty;

        public TwitchUser ToModel() => new()
        {
            Id = Id,
            Login = Login,
            DisplayName = DisplayName,
            ProfileImageUrl = ProfileImageUrl
        };
    }

    private sealed class ChannelSearchDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("broadcaster_login")]
        public string BroadcasterLogin { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("thumbnail_url")]
        public string ThumbnailUrl { get; set; } = string.Empty;

        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("is_live")]
        public bool IsLive { get; set; }

        [JsonPropertyName("started_at")]
        public string StartedAt { get; set; } = string.Empty;

        public ChannelSearchResult ToModel() => new()
        {
            Id = Id,
            BroadcasterLogin = BroadcasterLogin,
            DisplayName = DisplayName,
            ThumbnailUrl = ThumbnailUrl.Replace("{width}", "70", StringComparison.Ordinal).Replace("{height}", "70", StringComparison.Ordinal),
            GameName = GameName,
            Title = Title,
            IsLive = IsLive,
            StartedAt = ParseTimestamp(StartedAt)
        };
    }

    private sealed class SendChatMessageDto
    {
        [JsonPropertyName("message_id")]
        public string MessageId { get; set; } = string.Empty;

        [JsonPropertyName("is_sent")]
        public bool IsSent { get; set; }

        [JsonPropertyName("drop_reason")]
        public DropReasonDto? DropReason { get; set; }
    }

    private sealed class DropReasonDto
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class BadgeSetDto
    {
        [JsonPropertyName("set_id")]
        public string SetId { get; set; } = string.Empty;

        [JsonPropertyName("versions")]
        public List<BadgeVersionDto> Versions { get; set; } = [];

        public IEnumerable<TwitchBadgeDefinition> ToDefinitions()
        {
            foreach (var version in Versions)
            {
                if (string.IsNullOrWhiteSpace(SetId) || string.IsNullOrWhiteSpace(version.Id))
                {
                    continue;
                }

                yield return new TwitchBadgeDefinition(
                    SetId,
                    version.Id,
                    version.ImageUrl1x,
                    version.ImageUrl2x,
                    version.ImageUrl4x,
                    version.Title);
            }
        }
    }

    private sealed class BadgeVersionDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("image_url_1x")]
        public string ImageUrl1x { get; set; } = string.Empty;

        [JsonPropertyName("image_url_2x")]
        public string ImageUrl2x { get; set; } = string.Empty;

        [JsonPropertyName("image_url_4x")]
        public string ImageUrl4x { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;
    }

    private sealed class StreamDto
    {
        [JsonPropertyName("viewer_count")]
        public int ViewerCount { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("game_name")]
        public string GameName { get; set; } = string.Empty;

        [JsonPropertyName("started_at")]
        public DateTimeOffset? StartedAt { get; set; }
    }
}
