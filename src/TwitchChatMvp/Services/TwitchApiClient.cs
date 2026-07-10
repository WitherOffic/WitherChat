using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class TwitchApiClient : IChannelSearchService
{
    private static readonly TimeSpan ChannelSearchCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<string> _clientIdProvider;
    private readonly AuthService _authService;
    private readonly FileLogger _logger;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
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

    public bool IsOnlineSearchAvailable => _authService.HasAccessToken;

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
        var user = payload.Data.FirstOrDefault() ?? throw new InvalidOperationException("Twitch не вернул текущего пользователя.");
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
                DropMessage = "Twitch не вернул результат отправки."
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

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        var accessToken = await _authService.EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(requestFactory, accessToken, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        _logger.Warn("Twitch API returned 401. Browser OAuth tokens cannot be refreshed; asking user to sign in again.");
        await _authService.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
        accessToken = await _authService.EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(requestFactory, accessToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> requestFactory, string accessToken, CancellationToken cancellationToken)
    {
        var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Client-Id", GetClientIdOrThrow());
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private string GetClientIdOrThrow()
    {
        var clientId = _clientIdProvider().Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Укажите Twitch Client ID в настройках.");
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

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await TwitchApiException.FromResponseAsync(response).ConfigureAwait(false);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidOperationException("Twitch вернул пустой JSON.");
    }

    private sealed class HelixData<T>
    {
        [JsonPropertyName("data")]
        public List<T> Data { get; set; } = [];
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
            StartedAt = DateTimeOffset.TryParse(StartedAt, out var startedAt) ? startedAt : null
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
