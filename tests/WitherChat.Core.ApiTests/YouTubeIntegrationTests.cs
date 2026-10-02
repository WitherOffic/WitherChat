using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class YouTubeIntegrationTests
{
    private const string ClientId = "123456789012-example.apps.googleusercontent.com";

    [Fact]
    public void ReleaseBuildContainsDesktopOAuthClientId()
    {
        Assert.EndsWith(".apps.googleusercontent.com", YouTubeApplication.ClientId, StringComparison.Ordinal);
        Assert.DoesNotContain("example", YouTubeApplication.ClientId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DesktopAuthorizationUsesPkceLoopbackAndModerationScopes()
    {
        var uri = YouTubeAuthService.BuildAuthorizeUri(
            ClientId,
            new Uri("http://127.0.0.1:43210/"),
            "state-token",
            "pkce-challenge");

        Assert.Equal("accounts.google.com", uri.Host);
        Assert.Contains("response_type=code", uri.Query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A43210%2F", uri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge=pkce-challenge", uri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", uri.Query, StringComparison.Ordinal);
        Assert.Contains("youtube.readonly", Uri.UnescapeDataString(uri.Query), StringComparison.Ordinal);
        Assert.Contains("youtube.force-ssl", Uri.UnescapeDataString(uri.Query), StringComparison.Ordinal);
        Assert.Contains("access_type=offline", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopTokenExchangeUsesClientIdAndPkceWithoutAClientSecret()
    {
        using var content = new FormUrlEncodedContent(
            YouTubeAuthService.CreateAuthorizationCodeTokenParameters(
                ClientId,
                "authorization-code",
                "pkce-verifier",
                new Uri("http://127.0.0.1:43210/")));

        var body = await content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("client_id=" + Uri.EscapeDataString(ClientId), body, StringComparison.Ordinal);
        Assert.Contains("code=authorization-code", body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=pkce-verifier", body, StringComparison.Ordinal);
        Assert.Contains("grant_type=authorization_code", body, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A43210%2F", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAndValidationUseYouTubeReadOnlyContracts()
    {
        var requests = new List<RecordedRequest>();
        var handler = new RecordingHandler(async request =>
        {
            requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync()));
            if (request.Method == HttpMethod.Post)
            {
                return Json("""
                    {"access_token":"new-access","expires_in":3600,
                     "scope":"https://www.googleapis.com/auth/youtube.readonly"}
                    """);
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Contains("mine=true", request.RequestUri!.Query, StringComparison.Ordinal);
            return Json("""
                {"items":[{"id":"UC-own-channel","snippet":{"title":"Own channel",
                "customUrl":"@ownchannel","thumbnails":{"default":{"url":"https://example.test/avatar.jpg"}}}}]}
                """);
        });
        using var service = new YouTubeAuthService(handler);
        service.Configure(ClientId);
        var session = new YouTubeAuthSession(
            "old-access", "refresh-token", ClientId, "old", "Old", string.Empty, null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddMinutes(-1));

        var refreshed = await service.RefreshAsync(session, TestContext.Current.CancellationToken);

        Assert.Equal("new-access", refreshed.AccessToken);
        Assert.Equal("refresh-token", refreshed.RefreshToken);
        Assert.Equal("UC-own-channel", refreshed.ChannelId);
        Assert.Equal("Own channel", refreshed.ChannelTitle);
        Assert.Equal("@ownchannel", refreshed.ChannelHandle);
        Assert.Equal("https://example.test/avatar.jpg", refreshed.ThumbnailUri?.AbsoluteUri);
        Assert.Contains(requests, request => request.Method == HttpMethod.Post &&
                                             request.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal) &&
                                             request.Body.Contains("refresh_token=refresh-token", StringComparison.Ordinal));
        Assert.DoesNotContain(
            requests,
            request => request.Body.Contains("client_secret=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OAuthTokenErrorsPreserveGoogleCodeAndDescription()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_client\",\"error_description\":\"Unauthorized client\"}",
                Encoding.UTF8,
                "application/json")
        }));
        using var service = new YouTubeAuthService(handler);
        service.Configure(ClientId);
        var session = new YouTubeAuthSession(
            "old-access", "refresh-token", ClientId, "channel", "Channel", string.Empty, null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddMinutes(-1));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.RefreshAsync(session, TestContext.Current.CancellationToken));

        Assert.Contains("invalid_client", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Unauthorized client", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebOAuthClientWithoutSecretReturnsActionableDesktopClientError()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":"invalid_request","error_description":"client_secret is missing."}""",
                Encoding.UTF8,
                "application/json")
        }));
        using var service = new YouTubeAuthService(handler);
        service.Configure(ClientId);
        var session = new YouTubeAuthSession(
            "old-access", "refresh-token", ClientId, "channel", "Channel", string.Empty, null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddMinutes(-1));

        var exception = await Assert.ThrowsAsync<YouTubeOAuthClientConfigurationException>(() =>
            service.RefreshAsync(session, TestContext.Current.CancellationToken));

        Assert.Contains("Desktop app", exception.GetLocalizedMessage(useEnglish: true), StringComparison.Ordinal);
        Assert.Contains("Приложение для ПК", exception.GetLocalizedMessage(useEnglish: false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingChannelReturnsActionableErrorInsteadOfNullSourceFailure()
    {
        var handler = new RecordingHandler(request => Task.FromResult(request.Method == HttpMethod.Post
            ? Json("""
                {"access_token":"new-access","expires_in":3600,
                 "scope":"https://www.googleapis.com/auth/youtube.readonly"}
                """)
            : Json("{\"pageInfo\":{\"totalResults\":0}}")));
        using var service = new YouTubeAuthService(handler);
        service.Configure(ClientId);
        var session = new YouTubeAuthSession(
            "old-access", "refresh-token", ClientId, "channel", "Channel", string.Empty, null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddMinutes(-1));

        var exception = await Assert.ThrowsAsync<YouTubeChannelUnavailableException>(() =>
            service.RefreshAsync(session, TestContext.Current.CancellationToken));

        Assert.Contains("YouTube channel", exception.GetLocalizedMessage(useEnglish: true), StringComparison.Ordinal);
        Assert.Contains("Brand Account", exception.GetLocalizedMessage(useEnglish: false), StringComparison.Ordinal);
    }

    [Fact]
    public void LiveMessageConversionPreservesAuthorAvatarAndYouTubeRoles()
    {
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "My stream", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        var source = new YouTubeLiveChatClient.LiveChatMessageResource(
            "message-1",
            new YouTubeLiveChatClient.LiveChatMessageSnippet(
                "textMessageEvent",
                DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
                "Привет из YouTube",
                new YouTubeLiveChatClient.TextMessageDetails("Привет из YouTube")),
            new YouTubeLiveChatClient.LiveChatAuthorDetails(
                "UC-viewer",
                "Viewer",
                "https://example.test/viewer.jpg",
                IsChatOwner: false,
                IsChatModerator: true,
                IsChatSponsor: true));

        var message = YouTubeLiveChatClient.ConvertMessage(source, session);

        Assert.NotNull(message);
        Assert.True(message!.IsYouTubeMessage);
        Assert.Equal("youtube_UC-owner", message.Channel);
        Assert.Equal("UC-viewer", message.UserId);
        Assert.Equal("Viewer", message.DisplayName);
        Assert.Equal("Привет из YouTube", message.Text);
        Assert.Equal("https://example.test/viewer.jpg", message.UserProfileImageUri?.AbsoluteUri);
        Assert.Contains(message.Badges, badge => badge.SetId == "youtube");
        Assert.Contains(message.Badges, badge => badge.SetId == "moderator");
        Assert.Contains(message.Badges, badge => badge.SetId == "subscriber");
    }

    [Fact]
    public void GiftComboUpdatesReuseServerIdButProduceDistinctChatUpdates()
    {
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Stream", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        YouTubeLiveChatClient.LiveChatMessageResource CreateGift(int comboCount) => new(
            "gift-message",
            new YouTubeLiveChatClient.LiveChatMessageSnippet(
                "giftEvent",
                DateTimeOffset.Parse("2026-08-03T12:00:00Z"),
                string.Empty,
                null,
                new YouTubeLiveChatClient.GiftEventDetails(
                    new YouTubeLiveChatClient.GiftMetadata("Rose", comboCount, "Rose gift"))),
            new YouTubeLiveChatClient.LiveChatAuthorDetails(
                "UC-viewer", "Viewer", string.Empty, false, false, false));

        var first = YouTubeLiveChatClient.ConvertMessage(CreateGift(1), session);
        var second = YouTubeLiveChatClient.ConvertMessage(CreateGift(2), session);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("gift-message:gift:1", first!.Id);
        Assert.Equal("gift-message:gift:2", second!.Id);
        Assert.Contains("×2", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SuperChatConvertsToPaidStreamEvent()
    {
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Stream", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        var source = new YouTubeLiveChatClient.LiveChatMessageResource(
            "super-1",
            new YouTubeLiveChatClient.LiveChatMessageSnippet(
                "superChatEvent", DateTimeOffset.Parse("2026-08-13T12:00:00Z"), "Thank you", null,
                SuperChatDetails: new YouTubeLiveChatClient.SuperChatDetails(
                    9_990_000, "RUB", "9.99 RUB", "Thank you", 2)),
            new YouTubeLiveChatClient.LiveChatAuthorDetails(
                "UC-viewer", "Viewer", string.Empty, false, false, false));

        var streamEvent = YouTubeLiveChatClient.ConvertStreamEvent(source, session);

        Assert.NotNull(streamEvent);
        Assert.Equal(StreamEventKinds.SuperChat, streamEvent.Kind);
        Assert.Equal("9.99 RUB", streamEvent.AmountDisplay);
        Assert.True(streamEvent.IsPaid);
        Assert.Equal("Thank you", streamEvent.Message);
    }

    [Fact]
    public async Task YouTubeMessageKeepsItsPlatformInLogsAndObsOverlay()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WitherChat-youtube-output-test-" + Guid.NewGuid().ToString("N"));
        var message = new ChatMessage
        {
            Id = "youtube-output-message",
            Channel = "youtube_UC-owner",
            BroadcasterId = "UC-owner",
            UserId = "UC-viewer",
            UserLogin = "UC-viewer",
            DisplayName = "YouTube viewer",
            Text = "Hello from YouTube",
            Timestamp = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
            Platform = ChatPlatforms.YouTube,
            Badges = [new ChatBadge("broadcaster", "1", Title: "witherchat.youtube.owner")],
            Parts = [ChatMessagePart.PlainText("Hello from YouTube")]
        };
        var writer = new ChatLogWriter(directory);
        await using var overlay = new ObsOverlayServer();
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        try
        {
            writer.Enqueue(message);
            await writer.DisposeAsync();
            var logPath = Directory.EnumerateFiles(directory, "chat.jsonl", SearchOption.AllDirectories).Single();
            using (var document = JsonDocument.Parse(
                       (await File.ReadAllLinesAsync(logPath, TestContext.Current.CancellationToken)).Single()))
            {
                var root = document.RootElement;
                Assert.Equal(ChatPlatforms.YouTube, root.GetProperty("platform").GetString());
                Assert.Equal("youtube_UC-owner", root.GetProperty("channel").GetString());
                Assert.Equal("broadcaster", root.GetProperty("badges")[0].GetProperty("SetId").GetString());
            }

            await overlay.ConfigureAsync(
                true,
                new ObsOverlayOptions(
                    port, 12, 22, true, true, true, 0, true, true, true, 0, "flex-start", "TornBlack"));
            overlay.Publish(message);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(
                $"http://localhost:{port}/overlay/events",
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(
                TestContext.Current.CancellationToken));
            string? eventData = null;
            for (var index = 0; index < 5 && eventData is null; index++)
            {
                var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
                if (line?.StartsWith("data: ", StringComparison.Ordinal) == true)
                {
                    eventData = line[6..];
                }
            }
            Assert.NotNull(eventData);
            using var overlayDocument = JsonDocument.Parse(eventData);
            var overlayRoot = overlayDocument.RootElement;
            Assert.Equal("YouTube viewer", overlayRoot.GetProperty("user").GetString());
            Assert.Equal("Hello from YouTube", overlayRoot.GetProperty("text").GetString());
            Assert.Equal(
                "YouTube channel owner",
                overlayRoot.GetProperty("badges")[0].GetProperty("label").GetString());
            Assert.DoesNotContain(
                "witherchat.youtube.",
                eventData,
                StringComparison.Ordinal);
        }
        finally
        {
            await writer.DisposeAsync();
            await overlay.ConfigureAsync(
                false,
                new ObsOverlayOptions(
                    port, 12, 22, true, true, true, 0, true, true, true, 0, "flex-start", "TornBlack"));
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LiveClientFindsOwnActiveBroadcastAndPublishesChatMessages()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/youtube/v3/liveBroadcasts")
            {
                Assert.Contains("broadcastStatus=active", request.RequestUri!.Query, StringComparison.Ordinal);
                Assert.DoesNotContain("mine=", request.RequestUri.Query, StringComparison.Ordinal);
                return Task.FromResult(Json("""
                    {"items":[{"id":"video-1","snippet":{"title":"Live now","liveChatId":"chat-1"}}]}
                    """));
            }
            Assert.Equal("/youtube/v3/liveChat/messages", path);
            Assert.Contains("liveChatId=chat-1", request.RequestUri!.Query, StringComparison.Ordinal);
            return Task.FromResult(Json("""
                {"nextPageToken":"next-1","pollingIntervalMillis":10000,"items":[{
                  "id":"message-live-1",
                  "snippet":{"type":"textMessageEvent","publishedAt":"2026-08-03T12:00:00Z",
                    "displayMessage":"Live hello","textMessageDetails":{"messageText":"Live hello"}},
                  "authorDetails":{"channelId":"UC-viewer","displayName":"Viewer",
                    "profileImageUrl":"https://example.test/viewer.jpg","isChatOwner":false,
                    "isChatModerator":false,"isChatSponsor":false}}]}
                """));
        });
        using var authService = new YouTubeAuthService(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, handler);
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, eventArgs) => received.TrySetResult(eventArgs.Message);
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Own channel", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));

        await client.StartAsync(session, TestContext.Current.CancellationToken);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var broadcastTitle = client.BroadcastTitle;
        await client.StopAsync();

        Assert.Equal("Live hello", message.Text);
        Assert.True(message.IsYouTubeMessage);
        Assert.Equal("youtube_UC-owner", client.CurrentChannel);
        Assert.Equal("Live now", broadcastTitle);
    }

    [Fact]
    public async Task LiveClientKeepsCurrentChatAndPageTokenAcrossOneTransientPollFailure()
    {
        var broadcastRequests = 0;
        var pollRequests = 0;
        var pollQueries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref broadcastRequests);
                return Task.FromResult(Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live","liveChatId":"chat-1"}}]}"""));
            }

            pollQueries.Enqueue(request.RequestUri.Query);
            var poll = Interlocked.Increment(ref pollRequests);
            if (poll == 2)
            {
                throw new HttpRequestException("temporary network failure");
            }
            var id = poll == 1 ? "message-1" : "message-2";
            var token = poll == 1 ? "resume-token" : "resume-token-2";
            var json = """
                {"nextPageToken":"__TOKEN__","pollingIntervalMillis":1000,"items":[{
                  "id":"__ID__","snippet":{"type":"textMessageEvent","publishedAt":"2026-08-03T12:00:00Z",
                    "displayMessage":"__ID__","textMessageDetails":{"messageText":"__ID__"}},
                  "authorDetails":{"channelId":"UC-viewer","displayName":"Viewer","profileImageUrl":"",
                    "isChatOwner":false,"isChatModerator":false,"isChatSponsor":false}}]}
                """
                .Replace("__TOKEN__", token, StringComparison.Ordinal)
                .Replace("__ID__", id, StringComparison.Ordinal);
            return Task.FromResult(Json(json));
        });
        using var authService = new YouTubeAuthService(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, handler)
        {
            PollRetryDelayOverride = TimeSpan.FromMilliseconds(10)
        };
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<ChatConnectionState>();
        var receivedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, value) => statuses.Enqueue(value.State);
        client.MessageReceived += (_, value) =>
        {
            messages.Enqueue(value.Message.Id);
            if (messages.Count >= 2)
            {
                receivedBoth.TrySetResult();
            }
        };
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));

        await client.StartAsync(session, TestContext.Current.CancellationToken);
        await receivedBoth.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.StopAsync();

        Assert.Equal(1, Volatile.Read(ref broadcastRequests));
        Assert.Equal(["message-1", "message-2"], messages.ToArray());
        Assert.DoesNotContain(ChatConnectionState.Reconnecting, statuses);
        var queries = pollQueries.ToArray();
        Assert.DoesNotContain("pageToken", queries[0], StringComparison.Ordinal);
        Assert.Contains("pageToken=resume-token", queries[1], StringComparison.Ordinal);
        Assert.Contains("pageToken=resume-token", queries[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveClientRefreshesAnAccessTokenRejectedBeforeItsLocalExpiry()
    {
        var pollRequests = 0;
        var refreshRequests = 0;
        var authHandler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref refreshRequests);
                return Task.FromResult(Json("""
                    {"access_token":"fresh-access","expires_in":3600,
                     "scope":"https://www.googleapis.com/auth/youtube.readonly"}
                    """));
            }
            return Task.FromResult(Json("""
                {"items":[{"id":"UC-owner","snippet":{"title":"Owner","customUrl":"@owner"}}]}
                """));
        });
        var liveHandler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live","liveChatId":"chat-1"}}]}"""));
            }

            if (Interlocked.Increment(ref pollRequests) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Invalid Credentials","errors":[{"reason":"authError"}]}}""",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            Assert.Equal("fresh-access", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""
                {"nextPageToken":"next","pollingIntervalMillis":30000,"items":[{
                  "id":"message-after-refresh","snippet":{"type":"textMessageEvent",
                    "publishedAt":"2026-08-03T12:00:00Z","displayMessage":"Recovered",
                    "textMessageDetails":{"messageText":"Recovered"}},
                  "authorDetails":{"channelId":"UC-viewer","displayName":"Viewer","profileImageUrl":"",
                    "isChatOwner":false,"isChatModerator":false,"isChatSponsor":false}}]}
                """));
        });
        using var authService = new YouTubeAuthService(authHandler);
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, liveHandler)
        {
            AuthorizationRetryDelayOverride = TimeSpan.FromMilliseconds(10)
        };
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource<YouTubeAuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, value) => received.TrySetResult(value.Message);
        client.SessionUpdated += (_, value) => updated.TrySetResult(value.Session);
        var session = new YouTubeAuthSession(
            "locally-valid-but-rejected", "refresh-token", ClientId, "UC-owner", "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));

        await client.StartAsync(session, TestContext.Current.CancellationToken);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var refreshed = await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.StopAsync();

        Assert.Equal("message-after-refresh", message.Id);
        Assert.Equal("fresh-access", refreshed.AccessToken);
        Assert.Equal(1, Volatile.Read(ref refreshRequests));
        Assert.True(Volatile.Read(ref pollRequests) >= 2);
    }

    [Fact]
    public void RepeatedAuthorizationFailuresUseBoundedBackoff()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            YouTubeLiveChatClient.AdvanceAuthorizationRetryDelay(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            YouTubeLiveChatClient.AdvanceAuthorizationRetryDelay(TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            YouTubeLiveChatClient.AdvanceAuthorizationRetryDelay(TimeSpan.Zero));
    }

    [Fact]
    public async Task LiveClientBacksOffQuotaExhaustionWithoutDroppingTheCurrentChat()
    {
        var pollRequests = 0;
        var firstQuotaAt = DateTimeOffset.MinValue;
        var recoveredAt = DateTimeOffset.MinValue;
        var authHandler = new RecordingHandler(_ => Task.FromResult(Json(
            """{"items":[{"id":"UC-owner","snippet":{"title":"Owner","customUrl":"@owner"}}]}""")));
        var liveHandler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live","liveChatId":"chat-quota"}}]}"""));
            }

            if (Interlocked.Increment(ref pollRequests) == 1)
            {
                firstQuotaAt = DateTimeOffset.UtcNow;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Daily quota exceeded","errors":[{"reason":"quotaExceeded"}]}}""",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            recoveredAt = DateTimeOffset.UtcNow;
            return Task.FromResult(Json("""
                {"nextPageToken":"next","pollingIntervalMillis":30000,"items":[{
                  "id":"message-after-quota","snippet":{"type":"textMessageEvent",
                    "publishedAt":"2026-08-03T12:00:00Z","displayMessage":"Recovered",
                    "textMessageDetails":{"messageText":"Recovered"}},
                  "authorDetails":{"channelId":"UC-viewer","displayName":"Viewer","profileImageUrl":"",
                    "isChatOwner":false,"isChatModerator":false,"isChatSponsor":false}}]}
                """));
        });
        using var authService = new YouTubeAuthService(authHandler);
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, liveHandler)
        {
            QuotaRetryDelayOverride = TimeSpan.FromMilliseconds(80)
        };
        var states = new List<ChatConnectionState>();
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, value) => states.Add(value.State);
        client.MessageReceived += (_, value) => received.TrySetResult(value.Message);
        var session = new YouTubeAuthSession(
            "access", "refresh-token", ClientId, "UC-owner", "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));

        await client.StartAsync(session, TestContext.Current.CancellationToken);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.StopAsync();

        Assert.Equal("message-after-quota", message.Id);
        Assert.Equal(2, Volatile.Read(ref pollRequests));
        Assert.True(recoveredAt - firstQuotaAt >= TimeSpan.FromMilliseconds(60));
        Assert.Contains(ChatConnectionState.Reconnecting, states);
        Assert.Contains(ChatConnectionState.Connected, states);
    }

    [Fact]
    public async Task LiveClientBacksOffQuotaExhaustionWhileDiscoveringTheBroadcast()
    {
        var broadcastRequests = 0;
        var firstQuotaAt = DateTimeOffset.MinValue;
        var recoveredAt = DateTimeOffset.MinValue;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref broadcastRequests) == 1)
                {
                    firstQuotaAt = DateTimeOffset.UtcNow;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent(
                            """{"error":{"message":"Daily quota exceeded","errors":[{"reason":"dailyLimitExceeded"}]}}""",
                            Encoding.UTF8,
                            "application/json")
                    });
                }

                recoveredAt = DateTimeOffset.UtcNow;
                return Task.FromResult(Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live","liveChatId":"chat-after-quota"}}]}"""));
            }

            return Task.FromResult(Json("""
                {"nextPageToken":"next","pollingIntervalMillis":30000,"items":[{
                  "id":"message-after-discovery-quota","snippet":{"type":"textMessageEvent",
                    "publishedAt":"2026-08-03T12:00:00Z","displayMessage":"Recovered",
                    "textMessageDetails":{"messageText":"Recovered"}},
                  "authorDetails":{"channelId":"UC-viewer","displayName":"Viewer","profileImageUrl":"",
                    "isChatOwner":false,"isChatModerator":false,"isChatSponsor":false}}]}
                """));
        });
        using var authService = new YouTubeAuthService(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, handler)
        {
            QuotaRetryDelayOverride = TimeSpan.FromMilliseconds(80)
        };
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<ChatConnectionStatusEventArgs>();
        client.StatusChanged += (_, value) => statuses.Enqueue(value);
        client.MessageReceived += (_, value) => received.TrySetResult(value.Message);
        var session = new YouTubeAuthSession(
            "access", "refresh-token", ClientId, "UC-owner", "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));

        await client.StartAsync(session, TestContext.Current.CancellationToken);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await client.StopAsync();

        Assert.Equal("message-after-discovery-quota", message.Id);
        Assert.Equal(2, Volatile.Read(ref broadcastRequests));
        Assert.True(recoveredAt - firstQuotaAt >= TimeSpan.FromMilliseconds(60));
        Assert.All(
            statuses.Where(status => status.State != ChatConnectionState.Disconnected),
            status => Assert.Equal("youtube_UC-owner", status.Channel));
    }

    [Fact]
    public async Task ModerationDeletesMessagesAndCreatesAndRemovesYouTubeBans()
    {
        var requests = new System.Collections.Concurrent.ConcurrentQueue<RecordedRequest>();
        var handler = new RecordingHandler(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, body));
            return (request.Method, request.RequestUri!.AbsolutePath) switch
            {
                (_, "/youtube/v3/liveBroadcasts") => Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live now","liveChatId":"chat-1"}}]}"""),
                (var method, "/youtube/v3/liveChat/messages") when method == HttpMethod.Get => Json(
                    """{"pollingIntervalMillis":30000,"items":[]}"""),
                (var method, "/youtube/v3/liveChat/messages") when method == HttpMethod.Delete =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                (var method, "/youtube/v3/liveChat/bans") when method == HttpMethod.Post =>
                    Json("""{"id":"ban-42"}"""),
                (var method, "/youtube/v3/liveChat/bans") when method == HttpMethod.Delete =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var authService = new YouTubeAuthService(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, handler);
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Own channel", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope, YouTubeAuthService.ModerationScope],
            DateTimeOffset.UtcNow.AddHours(1));
        await client.StartAsync(session, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(3));
        var message = new ChatMessage
        {
            Id = "message-live-1",
            PlatformMessageId = "youtube-message-1",
            Channel = "youtube_UC-owner",
            BroadcasterId = "UC-owner",
            UserId = "UC-viewer",
            UserLogin = "UC-viewer",
            DisplayName = "Viewer",
            Text = "Message",
            Timestamp = DateTimeOffset.UtcNow,
            Platform = ChatPlatforms.YouTube
        };

        await client.DeleteMessageAsync(message, TestContext.Current.CancellationToken);
        var ban = await client.BanUserAsync(message, 600, TestContext.Current.CancellationToken);
        await client.RemoveBanAsync(ban.Id, TestContext.Current.CancellationToken);
        await client.StopAsync();

        Assert.Equal("ban-42", ban.Id);
        Assert.Equal("UC-viewer", ban.UserChannelId);
        var deleteMessage = Assert.Single(requests, value =>
            value.Method == HttpMethod.Delete && value.Uri.AbsolutePath.EndsWith("/liveChat/messages", StringComparison.Ordinal));
        Assert.Contains("id=youtube-message-1", deleteMessage.Uri.Query, StringComparison.Ordinal);
        var insertBan = Assert.Single(requests, value =>
            value.Method == HttpMethod.Post && value.Uri.AbsolutePath.EndsWith("/liveChat/bans", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(insertBan.Body);
        var snippet = body.RootElement.GetProperty("snippet");
        Assert.Equal("chat-1", snippet.GetProperty("liveChatId").GetString());
        Assert.Equal("temporary", snippet.GetProperty("type").GetString());
        Assert.Equal("600", snippet.GetProperty("banDurationSeconds").GetString());
        Assert.Equal("UC-viewer", snippet.GetProperty("bannedUserDetails").GetProperty("channelId").GetString());
        Assert.Contains(requests, value =>
            value.Method == HttpMethod.Delete &&
            value.Uri.AbsolutePath.EndsWith("/liveChat/bans", StringComparison.Ordinal) &&
            value.Uri.Query.Contains("id=ban-42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModerationRejectsLegacyReadOnlySessionBeforeSendingAWriteRequest()
    {
        var writes = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref writes);
            }
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal)
                ? Json("""{"items":[{"id":"video-1","snippet":{"title":"Live","liveChatId":"chat-1"}}]}""")
                : Json("""{"pollingIntervalMillis":30000,"items":[]}"""));
        });
        using var authService = new YouTubeAuthService(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, handler);
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        await client.StartAsync(session, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => client.IsConnected, TimeSpan.FromSeconds(3));
        var message = new ChatMessage
        {
            Id = "message-1",
            Channel = "youtube_UC-owner",
            UserId = "UC-viewer",
            UserLogin = "UC-viewer",
            DisplayName = "Viewer",
            Text = "Message",
            Timestamp = DateTimeOffset.UtcNow,
            Platform = ChatPlatforms.YouTube
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DeleteMessageAsync(message, TestContext.Current.CancellationToken));
        await client.StopAsync();

        Assert.Contains("moderation permission", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task TombstoneNotifiesTheUiEvenWhenTheOriginalMessageIdWasAlreadySeen()
    {
        var poll = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live","liveChatId":"chat-1"}}]}"""));
            }
            if (Interlocked.Increment(ref poll) == 1)
            {
                return Task.FromResult(Json("""
                    {"pollingIntervalMillis":1000,"items":[{
                      "id":"message-1","snippet":{"type":"textMessageEvent","publishedAt":"2026-08-03T12:00:00Z",
                      "displayMessage":"Hello","textMessageDetails":{"messageText":"Hello"}},
                      "authorDetails":{"channelId":"UC-viewer","displayName":"Viewer","profileImageUrl":"",
                      "isChatOwner":false,"isChatModerator":false,"isChatSponsor":false}}]}
                    """));
            }
            return Task.FromResult(Json("""
                {"pollingIntervalMillis":30000,"items":[{
                  "id":"message-1","snippet":{"type":"tombstone","publishedAt":"2026-08-03T12:00:01Z",
                  "displayMessage":""}}]}
                """));
        });
        using var authService = new YouTubeAuthService(new RecordingHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        authService.Configure(ClientId);
        await using var client = new YouTubeLiveChatClient(authService, handler);
        var deleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageDeleted += (_, eventArgs) => deleted.TrySetResult(eventArgs.MessageId);
        var session = new YouTubeAuthSession(
            "access", "refresh", ClientId, "UC-owner", "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));

        await client.StartAsync(session, TestContext.Current.CancellationToken);
        var messageId = await deleted.Task.WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        await client.StopAsync();

        Assert.Equal("message-1", messageId);
        Assert.True(Volatile.Read(ref poll) >= 2);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate());
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
