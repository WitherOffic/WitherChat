using System.Net;
using System.Text;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class TwitchApiContractTests
{
    [Fact]
    public void TwitchEmoteProvidesThemeAppropriateCdnVariants()
    {
        var animated = ChatMessagePart.TwitchEmote("Kappa", "25", isAnimated: true);

        Assert.Contains("/animated/dark/2.0", animated.GetImageUri(useLightTwitchTheme: false)!.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Contains("/animated/light/2.0", animated.GetImageUri(useLightTwitchTheme: true)!.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Equal(animated.ImageUri, animated.GetImageUri(useLightTwitchTheme: false));
    }

    [Fact]
    public void IrcMessagesPreserveSharedChatBadgesAndRecoverRoleFlags()
    {
        var parsed = TwitchIrcClient.TryParseChatMessage(
            "@badge-info=;badges=;source-badge-info=subscriber/18;" +
            "source-badges=moderator/1,subscriber/12;display-name=SharedMod;id=shared-1;" +
            "user-id=42;tmi-sent-ts=1710000000000 " +
            ":sharedmod!sharedmod@sharedmod.tmi.twitch.tv PRIVMSG #witherchat :shared hello",
            "fallback",
            out var sharedMessage);

        Assert.True(parsed);
        Assert.Collection(
            sharedMessage.Badges,
            badge => Assert.Equal("moderator/1", badge.Key),
            badge =>
            {
                Assert.Equal("subscriber/12", badge.Key);
                Assert.Equal("18", badge.Info);
            });

        parsed = TwitchIrcClient.TryParseChatMessage(
            "@badges=;display-name=RoleUser;id=roles-1;mod=1;subscriber=1;vip=1;turbo=1;" +
            "user-id=43;user-type=staff;tmi-sent-ts=1710000000000 " +
            ":roleuser!roleuser@roleuser.tmi.twitch.tv PRIVMSG #witherchat :roles hello",
            "fallback",
            out var roleMessage);

        Assert.True(parsed);
        Assert.Equal(
            ["moderator", "subscriber", "vip", "turbo", "staff"],
            roleMessage.Badges.Select(badge => badge.SetId));
    }

    [Fact]
    public async Task OAuthValidationAndRefreshUseExpectedContracts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath == "/oauth2/token")
            {
                return Json("""
                    {
                      "access_token": "refreshed-token",
                      "refresh_token": "refreshed-secret",
                      "expires_in": 3600,
                      "scope": ["user:read:chat"]
                    }
                    """);
            }

            Assert.Equal("/oauth2/validate", request.RequestUri?.AbsolutePath);
            Assert.Equal("OAuth", request.Headers.Authorization?.Scheme);
            return Json($$"""
                {
                  "client_id": "{{TwitchApplication.ClientId}}",
                  "login": "contract_user",
                  "user_id": "42",
                  "expires_in": 3600,
                  "scopes": [{{string.Join(",", TwitchApplication.RequiredScopes.Select(scope => $"\"{scope}\""))}}]
                }
                """);
        });
        using var service = new TwitchAuthService(handler: handler);
        var session = Session() with { RefreshToken = "refresh-secret" };

        var validated = await service.ValidateAsync(session, cancellationToken);
        var refreshed = await service.RefreshAsync(session, cancellationToken);

        Assert.Equal("42", validated.UserId);
        Assert.Equal("contract_user", validated.Login);
        Assert.Equal("refreshed-token", refreshed.AccessToken);
        Assert.Equal("refreshed-secret", refreshed.RefreshToken);
        Assert.Contains(TwitchApplication.ClipsScope, TwitchApplication.ChatScopes);
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Post &&
                       request.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal) &&
                       request.Body.Contains("refresh_token=refresh-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwitchHelixReadAndWriteMethodsMatchApiContracts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpHandler(RouteTwitchApi);
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId, handler);
        var session = Session();

        var search = await api.SearchChannelsAsync(session, "bra", cancellationToken);
        var followed = await api.GetFollowedChannelsAsync(session, cancellationToken);
        var publicChannel = await api.GetPublicChannelAsync("braeden", cancellationToken);
        var profile = await api.GetUserProfileAsync(session, "channel", cancellationToken);
        var stream = await api.GetStreamStatusAsync(session, "channel", cancellationToken);
        var badges = await api.GetBadgeCatalogAsync(session, "channel", cancellationToken);
        var moderation = await api.GetModerationAccessAsync(session, "channel", cancellationToken);
        var send = await api.SendMessageAsync(session, "channel", "hello", "parent-1", cancellationToken);
        var clip = await api.CreateClipAsync(session, "channel", cancellationToken);
        var message = new ChatMessage
        {
            Id = "message-1",
            Channel = "channel",
            BroadcasterId = "100",
            UserId = "200",
            UserLogin = "viewer",
            DisplayName = "Viewer",
            Text = "hello",
            Timestamp = DateTimeOffset.UtcNow
        };
        await api.DeleteMessageAsync(session, message, cancellationToken);
        await api.UpdateChatProtectionAsync(
            session, "channel", new ChatProtectionSettings(true, 15, true, true, 30), cancellationToken);
        await api.ClearChatAsync(session, "channel", cancellationToken);
        await api.BanOrTimeoutUserAsync(session, message, 600, "reason", cancellationToken);
        await api.RemovePunishmentAsync(session, message, cancellationToken);
        var banned = await api.GetBannedUsersAsync(session, "channel", "next", cancellationToken);
        await api.RemovePunishmentAsync(session, "channel", "200", cancellationToken);
        var appeals = await api.GetUnbanRequestsAsync(
            session,
            "channel",
            cancellationToken: cancellationToken);
        var resolved = await api.ResolveUnbanRequestAsync(
            session,
            "channel",
            "appeal-1",
            approve: true,
            "approved",
            cancellationToken);
        await api.CreateEventSubSubscriptionAsync(
            session,
            "ws-session",
            "channel.chat.message",
            "1",
            new Dictionary<string, string> { ["broadcaster_user_id"] = "100" },
            cancellationToken);
        await api.ManageHeldAutoModMessageAsync(session, "held-1", allow: true, cancellationToken);
        var pinned = await api.GetPinnedChatMessageAsync(session, "channel", cancellationToken);

        Assert.Equal(2, search.Count);
        Assert.Equal(
            2162,
            search.Single(result => result.BroadcasterLogin == "braeden").ViewerCount);
        Assert.Equal(2, followed.Count);
        Assert.Equal("followed_live", followed[0].BroadcasterLogin);
        Assert.True(followed[0].IsLive);
        Assert.Equal(777, followed[0].ViewerCount);
        Assert.Equal("https://example.test/followed-live.png", followed[0].ThumbnailUrl);
        Assert.NotNull(publicChannel);
        Assert.Equal("Braeden", publicChannel.DisplayName);
        Assert.Equal("100", profile.Id);
        Assert.True(stream.IsLive);
        Assert.Equal(321, stream.ViewerCount);
        Assert.Equal(2, badges.Count);
        Assert.True(moderation.CanModerate);
        Assert.True(moderation.IsModerator);
        Assert.True(send.IsSent);
        Assert.Equal("sent-1", send.MessageId);
        Assert.Equal("ContractClipSlug", clip.Id);
        Assert.Equal("https://clips.twitch.tv/ContractClipSlug", clip.ShareUri.AbsoluteUri);
        Assert.Equal("https://www.twitch.tv/channel/clip/ContractClipSlug", clip.EditUri.AbsoluteUri);
        Assert.Single(banned.Users);
        Assert.Equal("cursor-2", banned.Cursor);
        Assert.Single(appeals.Requests);
        Assert.Equal(UnbanRequestStatus.Approved, resolved.Status);
        Assert.NotNull(pinned);
        Assert.Equal("Pinned contract message", pinned.Text);

        Assert.All(
            handler.Requests.Where(request => request.Uri.Host == "api.twitch.tv"),
            request =>
            {
                Assert.Equal("Bearer", request.AuthorizationScheme);
                Assert.Equal(TwitchApplication.ClientId, request.ClientId);
            });
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Post &&
                       request.Uri.AbsolutePath == "/helix/chat/messages" &&
                       request.Body.Contains("\"reply_parent_message_id\":\"parent-1\"", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Post &&
                       request.Uri.AbsolutePath == "/helix/clips" &&
                       request.Uri.Query.Contains("broadcaster_id=100", StringComparison.Ordinal) &&
                       request.Body.Length == 0);
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Get &&
                       request.Uri.AbsolutePath == "/helix/clips" &&
                       request.Uri.Query.Contains("id=ContractClipSlug", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Post &&
                       request.Uri.AbsolutePath == "/helix/moderation/bans" &&
                       request.Body.Contains("\"duration\":600", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Patch &&
                       request.Uri.AbsolutePath == "/helix/chat/settings" &&
                       request.Body.Contains("\"slow_mode_wait_time\":15", StringComparison.Ordinal) &&
                       request.Body.Contains("\"follower_mode_duration\":30", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Delete &&
                       request.Uri.AbsolutePath == "/helix/moderation/chat" &&
                       !request.Uri.Query.Contains("message_id", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Method == HttpMethod.Patch &&
                       request.Uri.Query.Contains("status=approved", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Uri.AbsolutePath == "/helix/eventsub/subscriptions" &&
                       request.Body.Contains("\"session_id\":\"ws-session\"", StringComparison.Ordinal));
        Assert.Contains(
            handler.Requests,
            request => request.Uri.AbsolutePath == "/helix/moderation/automod/message" &&
                       request.Body.Contains("\"action\":\"ALLOW\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/helix/chat/badges/global", "subscriber/12")]
    [InlineData("/helix/chat/badges", "moderator/1")]
    public async Task BadgeCatalogKeepsTheAvailableHalfWhenOneEndpointFails(
        string failingPath,
        string expectedBadgeKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == failingPath)
            {
                return Json("""{"message":"temporarily unavailable"}""", HttpStatusCode.ServiceUnavailable);
            }

            return path switch
            {
                "/helix/users" => Json(
                    """{"data":[{"id":"100","login":"channel","display_name":"Channel"}]}"""),
                "/helix/chat/badges/global" => Json(
                    """{"data":[{"set_id":"moderator","versions":[{"id":"1","image_url_2x":"https://example.test/mod.png","title":"Moderator"}]}]}"""),
                "/helix/chat/badges" => Json(
                    """{"data":[{"set_id":"subscriber","versions":[{"id":"12","image_url_2x":"https://example.test/sub.png","title":"Subscriber"}]}]}"""),
                _ => Json("""{"data":[]}""")
            };
        });
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId, handler);

        var badges = await api.GetBadgeCatalogAsync(Session(), "channel", cancellationToken);

        Assert.Single(badges);
        Assert.True(badges.ContainsKey(expectedBadgeKey));
    }

    [Fact]
    public async Task ThirdPartyEmoteApisMergeGlobalAndChannelCatalogs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/3/cached/emotes/global" => Json("""
                    [{"id":"bttv-global","code":"OMEGALUL"},
                     {"id":"bttv-modifier","code":"c!","modifier":true}]
                    """),
                "/3/cached/users/twitch/100" => Json("""
                    {
                      "channelEmotes":[{"id":"bttv-channel","code":"monkaS"}],
                      "sharedEmotes":[]
                    }
                    """),
                "/v3/emote-sets/global" => Json("""
                    {
                      "emotes":[{
                        "id":"seven-global",
                        "name":"KEKW",
                        "data":{
                          "id":"seven-global",
                          "flags":0,
                          "host":{"url":"https://cdn.7tv.app/emote/seven-global","files":[
                            {"name":"2x.webp","width":64,"height":64}
                          ]}
                        }
                      }]
                    }
                    """),
                "/v3/users/twitch/100" => Json("""
                    {
                      "emote_set":{"emotes":[{
                        "id":"seven-channel",
                        "name":"widepeepoHappy",
                        "flags":256,
                        "data":{
                          "id":"seven-channel",
                          "flags":256,
                          "host":{"url":"//cdn.7tv.app/emote/seven-channel","files":[
                            {"name":"2x.gif","width":96,"height":64}
                          ]}
                        }
                      }]}
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var service = new ThirdPartyEmoteCatalogService(handler);

        var catalog = await service.LoadAsync("100", cancellationToken);

        Assert.Equal(4, catalog.Count);
        Assert.Equal("BTTV", catalog["OMEGALUL"].Provider);
        Assert.DoesNotContain("c!", catalog.Keys);
        Assert.Equal("7TV", catalog["KEKW"].Provider);
        Assert.True(catalog["widepeepoHappy"].IsZeroWidth);
        Assert.EndsWith("2x.gif", catalog["widepeepoHappy"].ImageUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task TwitchApiErrorsPreserveStatusAndMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpHandler(_ =>
            Json("""{"message":"token expired"}""", HttpStatusCode.Unauthorized));
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId, handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => api.GetStreamStatusAsync(Session(), "channel", cancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("token expired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateClipRequiresPermissionBeforeSendingAnyRequest()
    {
        var handler = new RecordingHttpHandler(_ =>
            throw new InvalidOperationException("No request should be sent without clips:edit."));
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId, handler);
        var session = Session() with
        {
            Scopes = TwitchApplication.RequiredScopes
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            api.CreateClipAsync(session, "channel", TestContext.Current.CancellationToken));

        Assert.Contains(TwitchApplication.ClipsScope, exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ThirdPartyEmoteCatalogRetriesTransientProviderFailure()
    {
        var failedOnce = false;
        var handler = new RecordingHttpHandler(request =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return request.RequestUri?.AbsolutePath switch
            {
                "/3/cached/emotes/global" => Json("[]"),
                "/v3/emote-sets/global" => Json("{\"emotes\":[]}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var service = new ThirdPartyEmoteCatalogService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.LoadAsync(string.Empty, TestContext.Current.CancellationToken));
        var catalog = await service.LoadAsync(string.Empty, TestContext.Current.CancellationToken);

        Assert.Empty(catalog);
        Assert.True(handler.Requests.Count >= 3);
    }

    private static HttpResponseMessage RouteTwitchApi(HttpRequestMessage request)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request URI is required.");
        var path = uri.AbsolutePath;
        var query = uri.Query;

        if (uri.Host == "gql.twitch.tv")
        {
            return Json("""
                {
                  "data":{"user":{
                    "id":"100","login":"braeden","displayName":"Braeden",
                    "profileImageURL":"https://example.test/avatar.png",
                    "stream":{"viewersCount":2162,"game":{"name":"IRL"}}
                  }}
                }
                """);
        }

        if (path == "/helix/search/channels")
        {
            return Json("""
                {
                  "data":[
                    {"id":"101","broadcaster_login":"brawlhalla","display_name":"Brawlhalla",
                     "thumbnail_url":"https://example.test/{width}x{height}.png","game_name":"Brawlhalla",
                     "title":"Live","is_live":false,"started_at":null},
                    {"id":"102","broadcaster_login":"braeden","display_name":"Braeden",
                     "thumbnail_url":"https://example.test/{width}x{height}.png","game_name":"IRL",
                     "title":"Live","is_live":true,"started_at":"2026-07-28T10:00:00Z"}
                  ],
                  "pagination":{}
                }
                """);
        }

        if (path == "/helix/channels/followed")
        {
            Assert.Contains("user_id=42", query, StringComparison.Ordinal);
            Assert.Contains("first=100", query, StringComparison.Ordinal);
            if (query.Contains("after=next-page", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "data":[
                        {"broadcaster_id":"202","broadcaster_login":"followed_live",
                         "broadcaster_name":"Followed Live","followed_at":"2026-07-21T10:00:00Z"}
                      ],
                      "pagination":{}
                    }
                    """);
            }
            return Json("""
                {
                  "data":[
                    {"broadcaster_id":"201","broadcaster_login":"followed_offline",
                     "broadcaster_name":"Followed Offline","followed_at":"2026-07-20T10:00:00Z"}
                  ],
                  "pagination":{"cursor":"next-page"}
                }
                """);
        }

        if (path == "/helix/users" && query.Contains("id=201", StringComparison.Ordinal))
        {
            return Json("""
                {"data":[
                  {"id":"201","login":"followed_offline","display_name":"Followed Offline",
                   "profile_image_url":"https://example.test/followed-offline.png"},
                  {"id":"202","login":"followed_live","display_name":"Followed Live",
                   "profile_image_url":"https://example.test/followed-live.png"}
                ]}
                """);
        }

        if (path == "/helix/streams" && query.Contains("user_id=201", StringComparison.Ordinal))
        {
            return Json("""
                {"data":[{"user_id":"202","viewer_count":777,"game_name":"IRL",
                "title":"Followed stream","started_at":"2026-07-28T10:00:00Z"}]}
                """);
        }

        if (path == "/helix/streams" && query.Contains("user_id=", StringComparison.Ordinal))
        {
            return Json("""{"data":[{"user_id":"102","viewer_count":2162}]}""");
        }

        if (path == "/helix/streams")
        {
            return Json("""{"data":[{"user_id":"100","viewer_count":321}]}""");
        }

        if (path == "/helix/users")
        {
            return Json("""
                {"data":[{"id":"100","login":"channel","display_name":"Channel",
                "profile_image_url":"https://example.test/channel.png"}]}
                """);
        }

        if (path == "/helix/chat/badges/global")
        {
            return Json("""
                {"data":[{"set_id":"moderator","versions":[{"id":"1",
                "image_url_1x":"https://example.test/mod.png","image_url_2x":"https://example.test/mod2.png",
                "image_url_4x":"https://example.test/mod4.png","title":"Moderator"}]}]}
                """);
        }

        if (path == "/helix/chat/badges")
        {
            return Json("""
                {"data":[{"set_id":"subscriber","versions":[{"id":"12",
                "image_url_1x":"https://example.test/sub.png","image_url_2x":"https://example.test/sub2.png",
                "image_url_4x":"https://example.test/sub4.png","title":"Subscriber"}]}]}
                """);
        }

        if (path == "/helix/moderation/channels")
        {
            return Json("""{"data":[{"broadcaster_id":"100"}],"pagination":{}}""");
        }

        if (path == "/helix/chat/messages")
        {
            return Json("""{"data":[{"message_id":"sent-1","is_sent":true,"drop_reason":null}]}""");
        }

        if (path == "/helix/clips" && request.Method == HttpMethod.Post)
        {
            return Json(
                """{"data":[{"id":"ContractClipSlug","edit_url":"https://www.twitch.tv/channel/clip/ContractClipSlug"}]}""",
                HttpStatusCode.Accepted);
        }

        if (path == "/helix/clips" && request.Method == HttpMethod.Get)
        {
            return Json("""{"data":[{"url":"https://clips.twitch.tv/ContractClipSlug"}]}""");
        }

        if (path == "/helix/moderation/banned")
        {
            return Json("""
                {
                  "data":[{"user_id":"200","user_login":"viewer","user_name":"Viewer",
                  "created_at":"2026-07-28T10:00:00Z","expires_at":null,"reason":"reason"}],
                  "pagination":{"cursor":"cursor-2"}
                }
                """);
        }

        if (path == "/helix/moderation/unban_requests")
        {
            var status = request.Method == HttpMethod.Patch ? "approved" : "pending";
            return Json($$"""
                {
                  "data":[{"id":"appeal-1","broadcaster_id":"100","user_id":"200",
                  "user_login":"viewer","user_name":"Viewer","text":"please",
                  "status":"{{status}}","created_at":"2026-07-28T10:00:00Z",
                  "resolved_at":null,"resolution_text":""}],
                  "pagination":{}
                }
                """);
        }

        if (path == "/helix/chat/pins")
        {
            return Json("""
                {
                  "data":[{
                    "message_id":"pin-1","broadcaster_id":"100","sender_user_id":"42",
                    "sender_user_login":"contract_user","sender_user_name":"Contract User",
                    "message":{"text":"Pinned contract message"},
                    "starts_at":"2026-07-28T10:00:00Z","ends_at":null,
                    "updated_at":"2026-07-28T10:00:00Z"
                  }],
                  "pagination":{}
                }
                """);
        }

        return new HttpResponseMessage(
            request.Method == HttpMethod.Post && path == "/helix/eventsub/subscriptions"
                ? HttpStatusCode.Accepted
                : HttpStatusCode.NoContent);
    }

    private static TwitchAuthSession Session() => new(
        "access-token",
        "refresh-token",
        TwitchApplication.ClientId,
        "42",
        "contract_user",
        TwitchApplication.ChatScopes,
        DateTimeOffset.UtcNow.AddHours(1),
        DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string Body,
        string AuthorizationScheme,
        string ClientId);

    private sealed class RecordingHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly object _sync = new();

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_sync)
            {
                Requests.Add(new RecordedRequest(
                    request.Method,
                    request.RequestUri ?? throw new InvalidOperationException("Request URI is missing."),
                    body,
                    request.Headers.Authorization?.Scheme ?? string.Empty,
                    request.Headers.TryGetValues("Client-Id", out var values)
                        ? values.Single()
                        : string.Empty));
            }
            return responder(request);
        }
    }
}
