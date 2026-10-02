using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class TwitchChatApiClient : IDisposable
{
    private static readonly Uri UsersEndpoint = new("https://api.twitch.tv/helix/users");
    private static readonly Uri SearchChannelsEndpoint = new("https://api.twitch.tv/helix/search/channels");
    private static readonly Uri StreamsEndpoint = new("https://api.twitch.tv/helix/streams");
    private static readonly Uri FollowedChannelsEndpoint = new("https://api.twitch.tv/helix/channels/followed");
    private static readonly Uri PublicGqlEndpoint = new("https://gql.twitch.tv/gql");
    // This is Twitch's public web client identifier, not an OAuth access token.
    private const string TwitchWebClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko"; // gitleaks:allow
    private static readonly Uri ChatMessagesEndpoint = new("https://api.twitch.tv/helix/chat/messages");
    private static readonly Uri ChatSettingsEndpoint = new("https://api.twitch.tv/helix/chat/settings");
    private static readonly Uri GlobalBadgesEndpoint = new("https://api.twitch.tv/helix/chat/badges/global");
    private static readonly Uri ChannelBadgesEndpoint = new("https://api.twitch.tv/helix/chat/badges");
    private static readonly Uri ModeratedChannelsEndpoint = new("https://api.twitch.tv/helix/moderation/channels");
    private static readonly Uri ModerationBansEndpoint = new("https://api.twitch.tv/helix/moderation/bans");
    private static readonly Uri ModerationChatEndpoint = new("https://api.twitch.tv/helix/moderation/chat");
    private static readonly Uri BannedUsersEndpoint = new("https://api.twitch.tv/helix/moderation/banned");
    private static readonly Uri UnbanRequestsEndpoint = new("https://api.twitch.tv/helix/moderation/unban_requests");
    private static readonly Uri EventSubSubscriptionsEndpoint = new("https://api.twitch.tv/helix/eventsub/subscriptions");
    private static readonly Uri AutoModMessageEndpoint = new("https://api.twitch.tv/helix/moderation/automod/message");
    private static readonly Uri ChatPinsEndpoint = new("https://api.twitch.tv/helix/chat/pins");
    private static readonly Uri ClipsEndpoint = new("https://api.twitch.tv/helix/clips");
    private static readonly TimeSpan ClipAvailabilityTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClipAvailabilityPollInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, TwitchUserProfile> _userProfileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string _clientId;

    public TwitchChatApiClient(string? clientId = null, HttpMessageHandler? handler = null)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? TwitchApplication.ClientId : clientId.Trim();
        _httpClient = handler is null
            ? new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    CheckCertificateRevocationList = true
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public void ConfigureClientId(string? clientId) =>
        _clientId = string.IsNullOrWhiteSpace(clientId) ? TwitchApplication.ClientId : clientId.Trim();

    public async Task<IReadOnlyList<ChannelSearchResult>> SearchChannelsAsync(
        TwitchAuthSession session,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedQuery = (query ?? string.Empty).Trim().TrimStart('@');
        if (normalizedQuery.Length < 2)
        {
            return [];
        }

        var endpoint = new Uri(
            SearchChannelsEndpoint +
            "?query=" + Uri.EscapeDataString(normalizedQuery) +
            "&live_only=false&first=6");
        using var request = CreateRequest(HttpMethod.Get, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<PagedResponse<ChannelSearchDto>>(response, cancellationToken)
            .ConfigureAwait(false);
        var results = payload.Data
            .Select(item => item.ToModel())
            .OrderBy(item => !string.Equals(
                item.BroadcasterLogin,
                normalizedQuery,
                StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();
        if (results.Length == 0)
        {
            return results;
        }

        var streamsQuery = string.Join(
            "&",
            results.Select(item => "user_id=" + Uri.EscapeDataString(item.Id)));
        using var streamsRequest = CreateRequest(
            HttpMethod.Get,
            new Uri(StreamsEndpoint + "?" + streamsQuery),
            session);
        using var streamsResponse = await _httpClient.SendAsync(streamsRequest, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(streamsResponse, cancellationToken).ConfigureAwait(false);
        var streams = await ReadAsync<StreamsResponse>(streamsResponse, cancellationToken).ConfigureAwait(false);
        var viewersByUserId = streams.Data.ToDictionary(
            item => item.UserId,
            item => Math.Max(0, item.ViewerCount),
            StringComparer.Ordinal);
        return results
            .Select(item => item with
            {
                ViewerCount = viewersByUserId.GetValueOrDefault(item.Id)
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<ChannelSearchResult>> GetFollowedChannelsAsync(
        TwitchAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.UserId))
        {
            throw new InvalidOperationException("The Twitch session does not contain a user id.");
        }

        var followedChannels = new List<FollowedChannelDto>();
        string? cursor = null;
        do
        {
            var query =
                "?user_id=" + Uri.EscapeDataString(session.UserId) +
                "&first=100" +
                (string.IsNullOrWhiteSpace(cursor)
                    ? string.Empty
                    : "&after=" + Uri.EscapeDataString(cursor));
            using var request = CreateRequest(HttpMethod.Get, new Uri(FollowedChannelsEndpoint + query), session);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var page = await ReadAsync<PagedResponse<FollowedChannelDto>>(response, cancellationToken)
                .ConfigureAwait(false);
            followedChannels.AddRange(page.Data);
            cursor = page.Data.Length == 0 ? null : page.Pagination?.Cursor;
        }
        while (!string.IsNullOrWhiteSpace(cursor));

        var ids = followedChannels
            .Select(channel => channel.BroadcasterId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var usersById = new Dictionary<string, TwitchUser>(StringComparer.Ordinal);
        var streamsByUserId = new Dictionary<string, StreamData>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(100))
        {
            var idsQuery = string.Join("&", batch.Select(id => "id=" + Uri.EscapeDataString(id)));
            using var usersRequest = CreateRequest(HttpMethod.Get, new Uri(UsersEndpoint + "?" + idsQuery), session);
            using var usersResponse = await _httpClient.SendAsync(usersRequest, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(usersResponse, cancellationToken).ConfigureAwait(false);
            var users = await ReadAsync<UsersResponse>(usersResponse, cancellationToken).ConfigureAwait(false);
            foreach (var user in users.Data)
            {
                usersById[user.Id] = user;
            }

            var streamsQuery = string.Join("&", batch.Select(id => "user_id=" + Uri.EscapeDataString(id)));
            using var streamsRequest = CreateRequest(
                HttpMethod.Get,
                new Uri(StreamsEndpoint + "?" + streamsQuery),
                session);
            using var streamsResponse = await _httpClient.SendAsync(streamsRequest, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(streamsResponse, cancellationToken).ConfigureAwait(false);
            var streams = await ReadAsync<StreamsResponse>(streamsResponse, cancellationToken).ConfigureAwait(false);
            foreach (var stream in streams.Data)
            {
                streamsByUserId[stream.UserId] = stream;
            }
        }

        return followedChannels
            .Where(channel => ids.Contains(channel.BroadcasterId, StringComparer.Ordinal))
            .Select(channel =>
            {
                usersById.TryGetValue(channel.BroadcasterId, out var user);
                streamsByUserId.TryGetValue(channel.BroadcasterId, out var stream);
                return new ChannelSearchResult(
                    channel.BroadcasterId,
                    channel.BroadcasterLogin,
                    channel.BroadcasterName,
                    user?.ProfileImageUrl ?? string.Empty,
                    stream?.GameName ?? string.Empty,
                    stream?.Title ?? string.Empty,
                    stream is not null,
                    stream?.StartedAt,
                    Math.Max(0, stream?.ViewerCount ?? 0));
            })
            .OrderByDescending(channel => channel.IsLive)
            .ThenByDescending(channel => channel.ViewerCount)
            .ThenBy(channel => channel.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<ChannelSearchResult?> GetPublicChannelAsync(
        string login,
        CancellationToken cancellationToken = default)
    {
        var normalizedLogin = NormalizeLogin(login);
        if (normalizedLogin.Length == 0)
        {
            return null;
        }

        const string query = """
            query ChannelLookup($login: String!) {
              user(login: $login) {
                id
                login
                displayName
                profileImageURL(width: 70)
                stream {
                  viewersCount
                  game { name }
                }
              }
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, PublicGqlEndpoint);
        request.Headers.TryAddWithoutValidation("Client-ID", TwitchWebClientId);
        request.Content = JsonContent.Create(
            new PublicChannelLookupRequest(
                "ChannelLookup",
                new PublicChannelLookupVariables(normalizedLogin),
                query),
            options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<PublicChannelLookupResponse>(response, cancellationToken)
            .ConfigureAwait(false);
        var user = payload.Data?.User;
        if (user is null || string.IsNullOrWhiteSpace(user.Id))
        {
            return null;
        }

        return new ChannelSearchResult(
            user.Id,
            string.IsNullOrWhiteSpace(user.Login) ? normalizedLogin : user.Login,
            string.IsNullOrWhiteSpace(user.DisplayName) ? normalizedLogin : user.DisplayName,
            user.ProfileImageUrl ?? string.Empty,
            user.Stream?.Game?.Name ?? string.Empty,
            string.Empty,
            user.Stream is not null,
            null,
            Math.Max(0, user.Stream?.ViewersCount ?? 0));
    }

    public async Task<SendChatMessageResult> SendMessageAsync(
        TwitchAuthSession session,
        string channelLogin,
        string message,
        string? replyParentMessageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedChannel = NormalizeLogin(channelLogin);
        var normalizedMessage = message ?? string.Empty;
        if (normalizedChannel.Length == 0)
        {
            throw new ArgumentException("A channel is required.", nameof(channelLogin));
        }

        if (string.IsNullOrWhiteSpace(normalizedMessage) || normalizedMessage.Length > 500)
        {
            throw new ArgumentException("A Twitch chat message must contain 1-500 characters.", nameof(message));
        }

        var broadcasterId = await ResolveUserIdAsync(session, normalizedChannel, cancellationToken).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Post, ChatMessagesEndpoint, session);
        request.Content = JsonContent.Create(
            new SendMessageRequest(
                broadcasterId,
                session.UserId,
                normalizedMessage,
                string.IsNullOrWhiteSpace(replyParentMessageId) ? null : replyParentMessageId),
            options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<SendMessageResponse>(response, cancellationToken).ConfigureAwait(false);
        var result = payload.Data.FirstOrDefault()
                     ?? throw new InvalidDataException("Twitch returned no send result.");
        return new SendChatMessageResult(
            result.IsSent,
            result.MessageId ?? string.Empty,
            result.DropReason?.Code ?? string.Empty,
            result.DropReason?.Message ?? string.Empty);
    }

    public async Task<TwitchClipCreationResult> CreateClipAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedChannel = NormalizeLogin(channelLogin);
        if (normalizedChannel.Length == 0)
        {
            throw new ArgumentException("A channel is required.", nameof(channelLogin));
        }

        if (!session.Scopes.Contains(TwitchApplication.ClipsScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The Twitch token is missing scope: " + TwitchApplication.ClipsScope);
        }

        var broadcasterId = await ResolveUserIdAsync(session, normalizedChannel, cancellationToken)
            .ConfigureAwait(false);
        var endpoint = new Uri(
            ClipsEndpoint + "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId));
        using var request = CreateRequest(HttpMethod.Post, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<CreateClipResponse>(response, cancellationToken).ConfigureAwait(false);
        var clip = payload.Data?.FirstOrDefault()
                   ?? throw new InvalidDataException("Twitch returned no created clip.");
        if (string.IsNullOrWhiteSpace(clip.Id) ||
            !Uri.TryCreate(clip.EditUrl, UriKind.Absolute, out var editUri) ||
            editUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(editUri.Host, "www.twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Twitch returned an invalid clip response.");
        }

        var shareUri = await WaitForCreatedClipAsync(clip.Id, session, cancellationToken).ConfigureAwait(false);
        return new TwitchClipCreationResult(clip.Id, editUri, shareUri);
    }

    private async Task<Uri> WaitForCreatedClipAsync(
        string clipId,
        TwitchAuthSession session,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ClipAvailabilityTimeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = new Uri(ClipsEndpoint + "?id=" + Uri.EscapeDataString(clipId));
            using var request = CreateRequest(HttpMethod.Get, endpoint, session);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var payload = await ReadAsync<GetClipsResponse>(response, cancellationToken).ConfigureAwait(false);
            var url = payload.Data?.FirstOrDefault()?.Url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var shareUri) &&
                shareUri.Scheme == Uri.UriSchemeHttps &&
                string.Equals(shareUri.Host, "clips.twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                return shareUri;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(ClipAvailabilityPollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException("Twitch did not finish creating the clip within 60 seconds.");
    }

    public async Task<IReadOnlyDictionary<string, TwitchBadgeDefinition>> GetBadgeCatalogAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedChannel = NormalizeLogin(channelLogin);
        if (normalizedChannel.Length == 0)
        {
            throw new ArgumentException("A channel is required.", nameof(channelLogin));
        }

        var globalTask = TryGetBadgesAsync(GlobalBadgesEndpoint, session, cancellationToken);
        var channelTask = TryGetChannelBadgesAsync(session, normalizedChannel, cancellationToken);
        var catalogs = await Task.WhenAll(globalTask, channelTask).ConfigureAwait(false);

        if (catalogs.All(result => !result.Succeeded))
        {
            throw new HttpRequestException(
                "Twitch badge catalogs are unavailable.",
                new AggregateException(catalogs.Select(result => result.Error!)));
        }

        var catalog = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase);
        AddBadgeDefinitions(catalog, catalogs[0].Badges);
        // Channel definitions intentionally replace a global definition with the same key.
        AddBadgeDefinitions(catalog, catalogs[1].Badges);
        return catalog;
    }

    public async Task<TwitchUserProfile> GetUserProfileAsync(
        TwitchAuthSession session,
        string login,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedLogin = NormalizeLogin(login);
        if (normalizedLogin.Length == 0)
        {
            throw new ArgumentException("A user login is required.", nameof(login));
        }

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_userProfileCache.TryGetValue(normalizedLogin, out var cached))
            {
                return cached;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        var endpoint = new Uri(UsersEndpoint + "?login=" + Uri.EscapeDataString(normalizedLogin));
        using var request = CreateRequest(HttpMethod.Get, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<UsersResponse>(response, cancellationToken).ConfigureAwait(false);
        var user = payload.Data.FirstOrDefault()
                   ?? throw new InvalidOperationException("Twitch user was not found: " + normalizedLogin);
        var profile = new TwitchUserProfile(
            user.Id,
            string.IsNullOrWhiteSpace(user.Login) ? normalizedLogin : user.Login,
            string.IsNullOrWhiteSpace(user.DisplayName) ? normalizedLogin : user.DisplayName,
            FirstValidUri(user.ProfileImageUrl));

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _userProfileCache[normalizedLogin] = profile;
        }
        finally
        {
            _cacheLock.Release();
        }

        return profile;
    }

    public async Task<TwitchStreamStatus> GetStreamStatusAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var normalizedChannel = NormalizeLogin(channelLogin);
        if (normalizedChannel.Length == 0)
        {
            throw new ArgumentException("A channel is required.", nameof(channelLogin));
        }

        var endpoint = new Uri(StreamsEndpoint + "?user_login=" + Uri.EscapeDataString(normalizedChannel));
        using var request = CreateRequest(HttpMethod.Get, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<StreamsResponse>(response, cancellationToken).ConfigureAwait(false);
        var stream = payload.Data.FirstOrDefault();
        return stream is null
            ? new TwitchStreamStatus(false, 0)
            : new TwitchStreamStatus(true, Math.Max(0, stream.ViewerCount));
    }

    public async Task<ChannelModerationAccess> GetModerationAccessAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcaster = await GetUserProfileAsync(session, channelLogin, cancellationToken).ConfigureAwait(false);
        if (string.Equals(broadcaster.Id, session.UserId, StringComparison.Ordinal))
        {
            return new ChannelModerationAccess(true, false, true);
        }

        if (!session.Scopes.Contains("user:read:moderated_channels", StringComparer.Ordinal))
        {
            return new ChannelModerationAccess(false, false, false, "missing_scope");
        }

        var cursor = string.Empty;
        do
        {
            var query = "?user_id=" + Uri.EscapeDataString(session.UserId) + "&first=100";
            if (cursor.Length > 0)
            {
                query += "&after=" + Uri.EscapeDataString(cursor);
            }

            using var request = CreateRequest(HttpMethod.Get, new Uri(ModeratedChannelsEndpoint + query), session);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var payload = await ReadAsync<ModeratedChannelsResponse>(response, cancellationToken).ConfigureAwait(false);
            if (payload.Data.Any(item => string.Equals(item.BroadcasterId, broadcaster.Id, StringComparison.Ordinal)))
            {
                return new ChannelModerationAccess(false, true, true);
            }

            cursor = payload.Pagination?.Cursor ?? string.Empty;
        }
        while (cursor.Length > 0);

        return new ChannelModerationAccess(false, false, true);
    }

    public async Task DeleteMessageAsync(
        TwitchAuthSession session,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        var broadcasterId = await ResolveBroadcasterIdAsync(session, message, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(ModerationChatEndpoint +
            "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
            "&moderator_id=" + Uri.EscapeDataString(session.UserId) +
            "&message_id=" + Uri.EscapeDataString(message.Id));
        using var request = CreateRequest(HttpMethod.Delete, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateChatProtectionAsync(
        TwitchAuthSession session,
        string channelLogin,
        ChatProtectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var endpoint = new Uri(ChatSettingsEndpoint +
            "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
            "&moderator_id=" + Uri.EscapeDataString(session.UserId));
        using var request = CreateRequest(HttpMethod.Patch, endpoint, session);
        var payload = new Dictionary<string, object>
        {
            ["slow_mode"] = settings.SlowMode,
            ["subscriber_mode"] = settings.SubscriberMode,
            ["follower_mode"] = settings.FollowerMode
        };
        if (settings.SlowMode)
        {
            payload["slow_mode_wait_time"] = Math.Clamp(settings.SlowModeWaitSeconds, 3, 120);
        }
        if (settings.FollowerMode)
        {
            payload["follower_mode_duration"] = Math.Clamp(settings.FollowerModeDurationMinutes, 0, 129_600);
        }
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearChatAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var endpoint = new Uri(ModerationChatEndpoint +
            "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
            "&moderator_id=" + Uri.EscapeDataString(session.UserId));
        using var request = CreateRequest(HttpMethod.Delete, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task BanOrTimeoutUserAsync(
        TwitchAuthSession session,
        ChatMessage message,
        int? durationSeconds,
        string reason = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        var broadcasterId = await ResolveBroadcasterIdAsync(session, message, cancellationToken).ConfigureAwait(false);
        var targetUserId = await ResolveTargetUserIdAsync(session, message, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(ModerationBansEndpoint +
            "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
            "&moderator_id=" + Uri.EscapeDataString(session.UserId));
        using var request = CreateRequest(HttpMethod.Post, endpoint, session);
        request.Content = JsonContent.Create(new
        {
            data = new
            {
                user_id = targetUserId,
                duration = durationSeconds,
                reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            }
        }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemovePunishmentAsync(
        TwitchAuthSession session,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        var broadcasterId = await ResolveBroadcasterIdAsync(session, message, cancellationToken).ConfigureAwait(false);
        var targetUserId = await ResolveTargetUserIdAsync(session, message, cancellationToken).ConfigureAwait(false);
        var endpoint = new Uri(ModerationBansEndpoint +
            "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
            "&moderator_id=" + Uri.EscapeDataString(session.UserId) +
            "&user_id=" + Uri.EscapeDataString(targetUserId));
        using var request = CreateRequest(HttpMethod.Delete, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BannedUsersPage> GetBannedUsersAsync(
        TwitchAuthSession session,
        string channelLogin,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var query = "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) + "&first=100";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += "&after=" + Uri.EscapeDataString(cursor);
        }
        using var request = CreateRequest(HttpMethod.Get, new Uri(BannedUsersEndpoint + query), session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var page = await ReadAsync<PagedResponse<BannedUserDto>>(response, cancellationToken).ConfigureAwait(false);
        return new BannedUsersPage(
            page.Data.Select(item => new BannedUser(
                item.UserId,
                item.UserLogin,
                item.UserName,
                item.CreatedAt,
                ParseOptionalTimestamp(item.ExpiresAt),
                item.Reason ?? string.Empty)).ToArray(),
            page.Pagination?.Cursor ?? string.Empty);
    }

    public async Task RemovePunishmentAsync(
        TwitchAuthSession session,
        string channelLogin,
        string targetUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var endpoint = new Uri(ModerationBansEndpoint +
            "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
            "&moderator_id=" + Uri.EscapeDataString(session.UserId) +
            "&user_id=" + Uri.EscapeDataString(targetUserId));
        using var request = CreateRequest(HttpMethod.Delete, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UnbanRequestsPage> GetUnbanRequestsAsync(
        TwitchAuthSession session,
        string channelLogin,
        UnbanRequestStatus status = UnbanRequestStatus.Pending,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var query = "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
                    "&moderator_id=" + Uri.EscapeDataString(session.UserId) +
                    "&status=" + status.ToString().ToLowerInvariant() + "&first=100";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query += "&after=" + Uri.EscapeDataString(cursor);
        }
        using var request = CreateRequest(HttpMethod.Get, new Uri(UnbanRequestsEndpoint + query), session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var page = await ReadAsync<PagedResponse<UnbanRequestDto>>(response, cancellationToken).ConfigureAwait(false);
        return new UnbanRequestsPage(page.Data.Select(ToUnbanRequest).ToArray(), page.Pagination?.Cursor ?? string.Empty);
    }

    public async Task<UnbanRequest> ResolveUnbanRequestAsync(
        TwitchAuthSession session,
        string channelLogin,
        string requestId,
        bool approve,
        string resolutionText = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var query = "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
                    "&moderator_id=" + Uri.EscapeDataString(session.UserId) +
                    "&unban_request_id=" + Uri.EscapeDataString(requestId) +
                    "&status=" + (approve ? "approved" : "denied");
        if (!string.IsNullOrWhiteSpace(resolutionText))
        {
            query += "&resolution_text=" + Uri.EscapeDataString(resolutionText.Trim());
        }
        using var request = CreateRequest(HttpMethod.Patch, new Uri(UnbanRequestsEndpoint + query), session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<PagedResponse<UnbanRequestDto>>(response, cancellationToken).ConfigureAwait(false);
        return payload.Data.Select(ToUnbanRequest).FirstOrDefault()
               ?? throw new InvalidDataException("Twitch returned an empty unban-request response.");
    }

    public async Task CreateEventSubSubscriptionAsync(
        TwitchAuthSession session,
        string sessionId,
        string subscriptionType,
        string version,
        IReadOnlyDictionary<string, string> condition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(condition);
        using var request = CreateRequest(HttpMethod.Post, EventSubSubscriptionsEndpoint, session);
        request.Content = JsonContent.Create(new
        {
            type = subscriptionType,
            version,
            condition,
            transport = new { method = "websocket", session_id = sessionId }
        }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task ManageHeldAutoModMessageAsync(
        TwitchAuthSession session,
        string messageId,
        bool allow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var request = CreateRequest(HttpMethod.Post, AutoModMessageEndpoint, session);
        request.Content = JsonContent.Create(new
        {
            user_id = session.UserId,
            msg_id = messageId,
            action = allow ? "ALLOW" : "DENY"
        }, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PinnedChatMessage?> GetPinnedChatMessageAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var broadcasterId = await ResolveUserIdAsync(session, NormalizeLogin(channelLogin), cancellationToken)
            .ConfigureAwait(false);
        var query = "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId) +
                    "&moderator_id=" + Uri.EscapeDataString(session.UserId);
        using var request = CreateRequest(HttpMethod.Get, new Uri(ChatPinsEndpoint + query), session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var payload = await ReadAsync<PagedResponse<PinnedChatMessageDto>>(response, cancellationToken)
            .ConfigureAwait(false);
        var value = payload.Data.FirstOrDefault();
        return value is null
            ? null
            : new PinnedChatMessage(
                value.MessageId, value.BroadcasterId, value.SenderUserId, value.SenderUserLogin,
                value.SenderUserName, value.Message.Text, value.StartsAt, value.EndsAt, value.UpdatedAt);
    }

    private static UnbanRequest ToUnbanRequest(UnbanRequestDto item) => new(
        item.Id,
        item.BroadcasterId,
        item.UserId,
        item.UserLogin,
        item.UserName,
        item.Text ?? string.Empty,
        item.CreatedAt,
        Enum.TryParse<UnbanRequestStatus>(item.Status, true, out var status) ? status : UnbanRequestStatus.Pending,
        item.ResolvedAt,
        item.ResolutionText ?? string.Empty);

    public void Dispose()
    {
        _httpClient.Dispose();
        _cacheLock.Dispose();
    }

    private async Task<string> ResolveUserIdAsync(
        TwitchAuthSession session,
        string login,
        CancellationToken cancellationToken)
    {
        return (await GetUserProfileAsync(session, login, cancellationToken).ConfigureAwait(false)).Id;
    }

    private async Task<string> ResolveBroadcasterIdAsync(
        TwitchAuthSession session,
        ChatMessage message,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(message.BroadcasterId)
            ? await ResolveUserIdAsync(session, message.Channel, cancellationToken).ConfigureAwait(false)
            : message.BroadcasterId;

    private async Task<string> ResolveTargetUserIdAsync(
        TwitchAuthSession session,
        ChatMessage message,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(message.UserId)
            ? await ResolveUserIdAsync(session, message.UserLogin, cancellationToken).ConfigureAwait(false)
            : message.UserId;

    private async Task<BadgeSet[]> GetBadgesAsync(
        Uri endpoint,
        TwitchAuthSession session,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, endpoint, session);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await ReadAsync<BadgeResponse>(response, cancellationToken).ConfigureAwait(false)).Data;
    }

    private async Task<BadgeFetchResult> TryGetChannelBadgesAsync(
        TwitchAuthSession session,
        string channelLogin,
        CancellationToken cancellationToken)
    {
        try
        {
            var broadcasterId = await ResolveUserIdAsync(session, channelLogin, cancellationToken)
                .ConfigureAwait(false);
            return await TryGetBadgesAsync(
                    new Uri(ChannelBadgesEndpoint + "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId)),
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableBadgeCatalogFailure(exception))
        {
            return new BadgeFetchResult([], exception);
        }
    }

    private async Task<BadgeFetchResult> TryGetBadgesAsync(
        Uri endpoint,
        TwitchAuthSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            return new BadgeFetchResult(
                await GetBadgesAsync(endpoint, session, cancellationToken).ConfigureAwait(false),
                null);
        }
        catch (Exception exception) when (IsRecoverableBadgeCatalogFailure(exception))
        {
            return new BadgeFetchResult([], exception);
        }
    }

    private static bool IsRecoverableBadgeCatalogFailure(Exception exception) =>
        exception is HttpRequestException or InvalidDataException or JsonException or InvalidOperationException;

    private static void AddBadgeDefinitions(
        Dictionary<string, TwitchBadgeDefinition> catalog,
        IEnumerable<BadgeSet> badgeSets)
    {
        foreach (var set in badgeSets)
        {
            if (string.IsNullOrWhiteSpace(set.SetId))
            {
                continue;
            }

            foreach (var version in set.Versions)
            {
                if (string.IsNullOrWhiteSpace(version.Id))
                {
                    continue;
                }

                var imageUri = FirstValidUri(version.ImageUrl2x, version.ImageUrl1x, version.ImageUrl4x);
                var definition = new TwitchBadgeDefinition(set.SetId, version.Id, imageUri, version.Title ?? string.Empty);
                catalog[definition.Key] = definition;
            }
        }
    }

    private static Uri? FirstValidUri(params string?[] values)
    {
        foreach (var value in values)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                return uri;
            }
        }

        return null;
    }

    private sealed record BadgeFetchResult(BadgeSet[] Badges, Exception? Error)
    {
        public bool Succeeded => Error is null;
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Twitch logins are canonically lowercase.")]
    private static string NormalizeLogin(string value) =>
        (value ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();

    internal static DateTimeOffset? ParseOptionalTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri,
        TwitchAuthSession session)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add("Client-Id", _clientId);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.ReasonPhrase ?? response.StatusCode.ToString();
        try
        {
            var error = await ReadAsync<ApiErrorResponse>(response, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error.Message))
            {
                message = error.Message;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
        }

        throw new HttpRequestException("Twitch API request failed: " + message, null, response.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Twitch returned an empty JSON response.");
    }

    private sealed record SendMessageRequest(
        [property: JsonPropertyName("broadcaster_id")] string BroadcasterId,
        [property: JsonPropertyName("sender_id")] string SenderId,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("reply_parent_message_id"),
         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyParentMessageId);

    private sealed record SendMessageResponse(
        [property: JsonPropertyName("data")] SendMessageData[] Data);

    private sealed record CreateClipResponse(
        [property: JsonPropertyName("data")] CreateClipData[]? Data);

    private sealed record CreateClipData(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("edit_url")] string EditUrl);

    private sealed record GetClipsResponse(
        [property: JsonPropertyName("data")] GetClipData[]? Data);

    private sealed record GetClipData(
        [property: JsonPropertyName("url")] string Url);

    private sealed record SendMessageData(
        [property: JsonPropertyName("message_id")] string? MessageId,
        [property: JsonPropertyName("is_sent")] bool IsSent,
        [property: JsonPropertyName("drop_reason")] DropReason? DropReason);

    private sealed record DropReason(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);

    private sealed record UsersResponse(
        [property: JsonPropertyName("data")] TwitchUser[] Data);

    private sealed record TwitchUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("profile_image_url")] string? ProfileImageUrl);

    private sealed record BadgeResponse(
        [property: JsonPropertyName("data")] BadgeSet[] Data);

    private sealed record StreamsResponse(
        [property: JsonPropertyName("data")] StreamData[] Data);

    private sealed record StreamData(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("viewer_count")] int ViewerCount,
        [property: JsonPropertyName("game_name")] string? GameName = null,
        [property: JsonPropertyName("title")] string? Title = null,
        [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt = null);

    private sealed record FollowedChannelDto(
        [property: JsonPropertyName("broadcaster_id")] string BroadcasterId,
        [property: JsonPropertyName("broadcaster_login")] string BroadcasterLogin,
        [property: JsonPropertyName("broadcaster_name")] string BroadcasterName,
        [property: JsonPropertyName("followed_at")] DateTimeOffset FollowedAt);

    private sealed record PublicChannelLookupRequest(
        [property: JsonPropertyName("operationName")] string OperationName,
        [property: JsonPropertyName("variables")] PublicChannelLookupVariables Variables,
        [property: JsonPropertyName("query")] string Query);

    private sealed record PublicChannelLookupVariables(
        [property: JsonPropertyName("login")] string Login);

    private sealed record PublicChannelLookupResponse(
        [property: JsonPropertyName("data")] PublicChannelLookupData? Data);

    private sealed record PublicChannelLookupData(
        [property: JsonPropertyName("user")] PublicChannelLookupUser? User);

    private sealed record PublicChannelLookupUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("profileImageURL")] string? ProfileImageUrl,
        [property: JsonPropertyName("stream")] PublicChannelLookupStream? Stream);

    private sealed record PublicChannelLookupStream(
        [property: JsonPropertyName("viewersCount")] int ViewersCount,
        [property: JsonPropertyName("game")] PublicChannelLookupGame? Game);

    private sealed record PublicChannelLookupGame(
        [property: JsonPropertyName("name")] string Name);

    private sealed record BadgeSet(
        [property: JsonPropertyName("set_id")] string SetId,
        [property: JsonPropertyName("versions")] BadgeVersion[] Versions);

    private sealed record BadgeVersion(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("image_url_1x")] string? ImageUrl1x,
        [property: JsonPropertyName("image_url_2x")] string? ImageUrl2x,
        [property: JsonPropertyName("image_url_4x")] string? ImageUrl4x,
        [property: JsonPropertyName("title")] string? Title);

    private sealed record ApiErrorResponse(
        [property: JsonPropertyName("message")] string Message);

    private sealed record ModeratedChannelsResponse(
        [property: JsonPropertyName("data")] ModeratedChannel[] Data,
        [property: JsonPropertyName("pagination")] Pagination? Pagination);

    private sealed record ModeratedChannel(
        [property: JsonPropertyName("broadcaster_id")] string BroadcasterId);

    private sealed record Pagination(
        [property: JsonPropertyName("cursor")] string? Cursor);

    private sealed record PagedResponse<T>(
        [property: JsonPropertyName("data")] T[] Data,
        [property: JsonPropertyName("pagination")] Pagination? Pagination);

    private sealed record BannedUserDto(
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("user_login")] string UserLogin,
        [property: JsonPropertyName("user_name")] string UserName,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] string? ExpiresAt,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record UnbanRequestDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("broadcaster_id")] string BroadcasterId,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("user_login")] string UserLogin,
        [property: JsonPropertyName("user_name")] string UserName,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("resolved_at")] DateTimeOffset? ResolvedAt,
        [property: JsonPropertyName("resolution_text")] string? ResolutionText);

    private sealed record ChannelSearchDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("broadcaster_login")] string BroadcasterLogin,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("thumbnail_url")] string ThumbnailUrl,
        [property: JsonPropertyName("game_name")] string GameName,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("is_live")] bool IsLive,
        [property: JsonPropertyName("started_at")] string? StartedAt)
    {
        public ChannelSearchResult ToModel() => new(
            Id,
            BroadcasterLogin,
            DisplayName,
            ThumbnailUrl
                .Replace("{width}", "70", StringComparison.Ordinal)
                .Replace("{height}", "70", StringComparison.Ordinal),
            GameName,
            Title,
            IsLive,
            ParseOptionalTimestamp(StartedAt));
    }

    private sealed record PinnedChatMessageDto(
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("broadcaster_id")] string BroadcasterId,
        [property: JsonPropertyName("sender_user_id")] string SenderUserId,
        [property: JsonPropertyName("sender_user_login")] string SenderUserLogin,
        [property: JsonPropertyName("sender_user_name")] string SenderUserName,
        [property: JsonPropertyName("message")] PinnedChatMessageBodyDto Message,
        [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
        [property: JsonPropertyName("ends_at")] DateTimeOffset? EndsAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

    private sealed record PinnedChatMessageBodyDto([property: JsonPropertyName("text")] string Text);
}
