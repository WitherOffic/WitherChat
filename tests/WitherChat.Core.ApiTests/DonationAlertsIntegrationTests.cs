using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class DonationAlertsIntegrationTests
{
    [Fact]
    public void ReleaseBuildContainsDonationAlertsOAuthApplicationId() =>
        Assert.Equal("20400", DonationAlertsApplication.ClientId);

    [Fact]
    public void AuthorizationUsesImplicitFlowAndMinimumRealtimeScopes()
    {
        var uri = DonationAlertsAuthService.BuildAuthorizeUri(
            new Uri(DonationAlertsApplication.RedirectUri),
            "state-token",
            "123456");

        Assert.Equal("www.donationalerts.com", uri.Host);
        Assert.Contains("response_type=token", uri.Query, StringComparison.Ordinal);
        Assert.Contains("client_id=123456", uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(DonationAlertsApplication.RedirectUri), uri.Query, StringComparison.Ordinal);
        var decoded = Uri.UnescapeDataString(uri.Query);
        Assert.Contains(DonationAlertsApplication.UserScope, decoded, StringComparison.Ordinal);
        Assert.Contains(DonationAlertsApplication.DonationSubscribeScope, decoded, StringComparison.Ordinal);
        Assert.Contains(DonationAlertsApplication.DonationIndexScope, decoded, StringComparison.Ordinal);
        Assert.Contains("state=state-token", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void OAuthGrantedScopesAreParsedAndMissingPermissionIsDetected()
    {
        var complete = DonationAlertsAuthService.ParseGrantedScopes(
            "oauth-user-show oauth-donation-subscribe oauth-donation-index oauth-user-show");
        var incomplete = DonationAlertsAuthService.ParseGrantedScopes(
            "oauth-user-show oauth-donation-subscribe");

        Assert.Equal(DonationAlertsApplication.RequiredScopes, complete);
        Assert.True(DonationAlertsApplication.HasRequiredScopes(complete));
        Assert.False(DonationAlertsApplication.HasRequiredScopes(incomplete));
        Assert.Empty(DonationAlertsAuthService.ParseGrantedScopes("  "));
    }

    [Fact]
    public async Task DonationHistoryLoadsEveryPageAndPreservesDonationMetadata()
    {
        var requests = 0;
        var handler = new RecordingHandler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/alerts/donations", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(requests == 1
                ? Json("""
                    {"data":[{"id":102,"name":"donation","username":"New viewer",
                    "message_type":"text","message":"New message","amount":"500.50",
                    "currency":"rub","is_shown":1,"created_at":"2026-08-10 10:30:00",
                    "shown_at":"2026-08-10 10:31:00"}],
                    "links":{"next":"https://www.donationalerts.com/api/v1/alerts/donations?page=2"}}
                    """)
                : Json("""
                    {"data":[{"id":101,"name":"donation","username":"Older viewer",
                    "message_type":"audio","message":"Older message","amount":25,
                    "currency":"USD","is_shown":0,"created_at":"2026-08-09 08:00:00",
                    "shown_at":null}],"links":{"next":null}}
                    """));
        });
        await using var client = new DonationAlertsClient(handler);
        var session = new DonationAlertsAuthSession(
            "access-token", "20400", 42, "streamer", "Streamer", null,
            DonationAlertsApplication.RequiredScopes, DateTimeOffset.UtcNow.AddHours(1));

        var donations = await client.GetDonationHistoryAsync(
            session,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, requests);
        Assert.Equal(["102", "101"], donations.Select(item => item.Id));
        Assert.Equal("RUB", donations[0].Currency);
        Assert.Equal(500.50m, donations[0].Amount);
        Assert.True(donations[0].IsShown);
        Assert.NotNull(donations[0].ShownAtUtc);
        Assert.Equal("audio", donations[1].MessageType);
        Assert.False(donations[1].IsShown);
    }

    [Fact]
    public async Task ConcurrentStartsKeepExactlyOneRealtimeReconnectLoop()
    {
        var handler = new BlockingSocketTokenHandler();
        await using var client = new DonationAlertsClient(handler);
        var session = new DonationAlertsAuthSession(
            "access-token", "20400", 42, "streamer", "Streamer", null,
            DonationAlertsApplication.RequiredScopes, DateTimeOffset.UtcNow.AddHours(1));
        var lifecycleGate = (SemaphoreSlim)typeof(DonationAlertsClient)
            .GetField("_lifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

        await lifecycleGate.WaitAsync(TestContext.Current.CancellationToken);
        var firstStart = client.StartAsync(session, TestContext.Current.CancellationToken);
        var secondStart = client.StartAsync(session, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        lifecycleGate.Release();

        await Task.WhenAll(firstStart, secondStart);
        await WaitForAsync(() => handler.RequestCount >= 2 && handler.ActiveRequests == 1);

        Assert.Equal(1, handler.MaximumConcurrentRequests);
        Assert.Equal(1, handler.ActiveRequests);
        await client.StopAsync();
        Assert.Equal(0, handler.ActiveRequests);
    }

    [Theory]
    [InlineData("http://localhost:17656/", true)]
    [InlineData("http://127.0.0.1:17656/", true)]
    [InlineData("https://localhost:17656/", false)]
    [InlineData("http://example.com:17656/", false)]
    public void CallbackOnlyAcceptsLocalHttpLoopback(string value, bool expected) =>
        Assert.Equal(expected, DonationAlertsAuthService.IsTrustedLoopbackRedirectUri(new Uri(value)));

    [Fact]
    public async Task StoredSessionValidationUsesBearerUserContract()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/user/oauth", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json("""
                {"data":{"id":42,"code":"streamer","name":"Streamer",
                 "avatar":"https://example.test/avatar.png","socket_connection_token":"socket-token"}}
                """));
        });
        using var service = new DonationAlertsAuthService(handler);
        service.Configure("123456");
        string[] storedScopes =
            [DonationAlertsApplication.UserScope, DonationAlertsApplication.DonationSubscribeScope];
        var stored = new DonationAlertsAuthSession(
            "access-token", "123456", 42, "old", "Old", null,
            storedScopes, DateTimeOffset.UtcNow.AddHours(1));

        var validated = await service.ValidateAsync(stored, TestContext.Current.CancellationToken);

        Assert.Equal(42, validated.UserId);
        Assert.Equal("streamer", validated.UserCode);
        Assert.Equal("Streamer", validated.DisplayName);
        Assert.Equal("https://example.test/avatar.png", validated.AvatarUri?.AbsoluteUri);
        Assert.Equal(storedScopes, validated.Scopes);
    }

    [Theory]
    [InlineData("""
        {"result":{"channel":"$alerts:donation_42","data":{"data":{
          "id":101,"name":"donation","username":"Viewer","message":"Спасибо!",
          "amount":"500.50","currency":"RUB"}}}}
        """)]
    [InlineData("""
        {"push":{"pub":{"data":{"id":"102","name":"donation","username":"Viewer 2",
          "message":"Hello","amount":25,"currency":"USD"}}}}
        """)]
    public void RealtimePayloadParserPreservesSenderTextAmountAndCurrency(string payload)
    {
        var parsed = DonationAlertsClient.TryParseDonation(payload, out var donation);

        Assert.True(parsed);
        Assert.NotNull(donation);
        Assert.StartsWith("Viewer", donation.Username, StringComparison.Ordinal);
        Assert.NotEmpty(donation.Message);
        Assert.True(donation.Amount > 0);
        Assert.Contains(donation.Currency, new[] { "RUB", "USD" });
    }

    [Fact]
    public void RealtimePayloadParserIgnoresUnrelatedCentrifugoMessages()
    {
        Assert.False(DonationAlertsClient.TryParseDonation(
            "{\"result\":{\"client\":\"connection-id\"}}",
            out var donation));
        Assert.Null(donation);
    }

    [Fact]
    public void CentrifugoFramesAreSplitWithoutDroppingHeartbeatOrDonationPayloads()
    {
        var messages = DonationAlertsClient.SplitProtocolMessages(
            "{}\n{\"result\":{\"client\":\"connection-id\"}}\r\n" +
            "{\"push\":{\"pub\":{\"data\":{\"id\":\"102\"}}}}\n");

        Assert.Equal(3, messages.Count);
        Assert.Equal("{}", messages[0]);
        Assert.Equal("connection-id", DonationAlertsClient.ReadHandshakeClientId(messages[1]));
        Assert.Contains("\"id\":\"102\"", messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void CentrifugoSubscriptionIsConnectedOnlyAfterExpectedServerConfirmation()
    {
        const string channel = "$alerts:donation_42";
        Assert.True(DonationAlertsClient.IsValidSubscriptionAcknowledgement(
            """
            {"id":2,"result":{"type":1,"channel":"$alerts:donation_42",
             "data":{"info":{"user":"42","client":"client-id"}}}}
            """,
            channel));
        Assert.True(DonationAlertsClient.IsValidSubscriptionAcknowledgement(
            """
            {"result":{"type":1,"channel":"$alerts:donation_42",
             "data":{"info":{"user":"42","client":"client-id"}}}}
            """,
            channel));
        Assert.True(DonationAlertsClient.IsValidSubscriptionAcknowledgement(
            """{"id":2,"result":{}}""",
            channel));

        Assert.Throws<InvalidDataException>(() =>
            DonationAlertsClient.IsValidSubscriptionAcknowledgement(
                "{\"id\":2,\"error\":{\"code\":103,\"message\":\"permission denied\"}}",
                channel));
        Assert.Throws<InvalidDataException>(() =>
            DonationAlertsClient.IsValidSubscriptionAcknowledgement(
                "{\"id\":2,\"result\":{\"type\":1,\"channel\":\"$alerts:donation_99\"}}",
                channel));
        Assert.Throws<InvalidDataException>(() =>
            DonationAlertsClient.IsValidSubscriptionAcknowledgement("[]", channel));

        Assert.False(DonationAlertsClient.IsValidSubscriptionAcknowledgement(
            "{\"result\":{\"channel\":\"$alerts:donation_42\",\"data\":{}}}",
            channel));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"result\":[]}")]
    [InlineData("{\"result\":{\"client\":null}}")]
    public void MalformedCentrifugoHandshakeDoesNotThrowShapeErrors(string payload) =>
        Assert.Equal(string.Empty, DonationAlertsClient.ReadHandshakeClientId(payload));

    [Fact]
    public void ExplicitCentrifugoHandshakeFailureIsReportedImmediately()
    {
        Assert.Throws<InvalidDataException>(() => DonationAlertsClient.ReadHandshakeClientId(
            "{\"id\":1,\"error\":{\"code\":103,\"message\":\"permission denied\"}}"));
        Assert.Throws<InvalidDataException>(() => DonationAlertsClient.ReadHandshakeClientId(
            "{\"id\":1,\"result\":{}}"));
    }

    [Fact]
    public async Task ObsControllerUsesAlertsWidgetTokenForRealRepeatAndSkipCommands()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-da-obs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "STREAM.json"),
                """
                {"sources":[{"name":"DONATE","id":"browser_source","settings":{
                  "url":"https://www.donationalerts.com/widget/alerts?token=WidgetToken_1234567890"
                }}]}
                """,
                TestContext.Current.CancellationToken);
            var tokenStore = new MemoryTokenStore();
            var socketClient = new RecordingWidgetSocketClient();
            using var controller = new DonationAlertsObsController(
                directory,
                socketClient,
                tokenStore,
                stableReadyDuration: TimeSpan.FromMilliseconds(20));
            var donation = new DonationAlert(
                "30530030", "Viewer", "Message", 500, "RUB", DateTimeOffset.UtcNow);

            Assert.True(controller.IsConfigured);
            Assert.Equal("DONATE", controller.SourceName);
            await controller.RepeatDonationAsync(donation, TestContext.Current.CancellationToken);
            await controller.SkipDonationAsync(donation, TestContext.Current.CancellationToken);

            Assert.Equal("WidgetToken_1234567890", socketClient.StartedToken);
            Assert.Equal(30530030, socketClient.StartedAlertId);
            Assert.Equal("WidgetToken_1234567890", socketClient.SkippedToken);
            Assert.Equal(30530030, socketClient.SkippedAlertId);

            await WaitForAsync(() => tokenStore.Load() is not null);

            File.Delete(Path.Combine(directory, "STREAM.json"));
            var cachedSocketClient = new RecordingWidgetSocketClient();
            using var cachedController = new DonationAlertsObsController(
                directory,
                cachedSocketClient,
                tokenStore);
            Assert.True(cachedController.IsConfigured);
            Assert.Equal(string.Empty, cachedController.SourceName);
            await cachedController.RepeatDonationAsync(
                donation,
                TestContext.Current.CancellationToken);
            Assert.Equal("WidgetToken_1234567890", cachedSocketClient.StartedToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ObsControllerReconnectsTransportWithoutRepeatingAnAlert()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-da-reconnect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "STREAM.json"),
                """
                {"sources":[{"name":"DONATE","id":"browser_source","settings":{
                  "url":"https://www.donationalerts.com/widget/alerts?token=WidgetToken_1234567890"
                }}]}
                """,
                TestContext.Current.CancellationToken);
            var socketClient = new RecordingWidgetSocketClient();
            using var controller = new DonationAlertsObsController(
                directory,
                socketClient,
                new MemoryTokenStore());
            await controller.EnsureConnectedAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, socketClient.PrepareCalls);

            socketClient.PrepareFailuresRemaining = 2;
            for (var index = 0; index < 200; index++)
            {
                socketClient.RaiseConnectionLost();
            }
            await WaitForAsync(() => socketClient.IsConnected && socketClient.PrepareCalls >= 4);

            Assert.Equal(4, socketClient.PrepareCalls);
            Assert.Equal(0, socketClient.StartCalls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectionLossRacingStableTokenPersistenceStillSchedulesReconnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-da-stable-race-" + Guid.NewGuid().ToString("N"));
        var tokenStore = new BlockingTokenStore();
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "STREAM.json"),
                "{\"sources\":[{\"settings\":{\"url\":\"https://www.donationalerts.com/widget/alerts?token=WidgetToken_1234567890\"}}]}",
                TestContext.Current.CancellationToken);
            var socketClient = new RecordingWidgetSocketClient();
            using var controller = new DonationAlertsObsController(
                directory,
                socketClient,
                tokenStore,
                stableReadyDuration: TimeSpan.FromMilliseconds(20),
                initialReconnectDelay: TimeSpan.FromMilliseconds(5),
                readyWaitTimeout: TimeSpan.FromSeconds(1));

            await controller.EnsureConnectedAsync(TestContext.Current.CancellationToken);
            await tokenStore.SaveStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            socketClient.RaiseConnectionLost();
            tokenStore.ReleaseSave.TrySetResult();

            await WaitForAsync(() => socketClient.IsConnected && socketClient.PrepareCalls >= 2);
            Assert.Equal(2, socketClient.PrepareCalls);
        }
        finally
        {
            tokenStore.ReleaseSave.TrySetResult();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("<script>new SocketIOClientProxy({ socketHost: 'wss://socket4.donationalerts.com:443' })</script>", true)]
    [InlineData("<script>new SocketIOClientProxy({ socketHost: 'wss://socket25.donationalerts.com:443' })</script>", true)]
    [InlineData("<script>new SocketIOClientProxy({ socketHost: 'wss://socket4.donationalerts.com.evil.test:443' })</script>", false)]
    [InlineData("<script>new SocketIOClientProxy({ socketHost: 'ws://socket4.donationalerts.com:443' })</script>", false)]
    [InlineData("<script>new SocketIOClientProxy({ socketHost: 'wss://socket4.donationalerts.com:444' })</script>", false)]
    public void WidgetSocketHostParserAcceptsOnlyTrustedDonationAlertsServers(
        string html,
        bool expected)
    {
        var parsed = DonationAlertsWidgetSocketClient.TryReadSocketHost(html, out var socketHost);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.EndsWith(".donationalerts.com", socketHost.Host, StringComparison.Ordinal);
            Assert.Equal(Uri.UriSchemeWss, socketHost.Scheme);
            Assert.Equal(443, socketHost.Port);
        }
    }

    [Fact]
    public void WidgetSocketReadsEngineIoV3HeartbeatFromOpenPacket()
    {
        var parsed = DonationAlertsWidgetSocketClient.TryReadEngineHeartbeatSettings(
            """0{"sid":"socket-id","pingInterval":25000,"pingTimeout":60000}""",
            out var settings);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromSeconds(25), settings.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), settings.PingTimeout);
        Assert.False(DonationAlertsWidgetSocketClient.TryReadEngineHeartbeatSettings(
            """0{"sid":"socket-id","pingInterval":0,"pingTimeout":60000}""",
            out _));
        Assert.False(DonationAlertsWidgetSocketClient.TryReadEngineHeartbeatSettings("40", out _));
    }

    [Fact]
    public void WidgetSocketUsesTheCorrectNamespaceHandshakeForItsEngineIoVersion()
    {
        Assert.False(DonationAlertsWidgetSocketClient.ShouldSendNamespaceConnectPacket(3));
        Assert.True(DonationAlertsWidgetSocketClient.ShouldSendNamespaceConnectPacket(4));
    }

    [Fact]
    public async Task WidgetSocketPublishesReadyOnlyAfterThePostAuthenticationPong()
    {
        var socket = new ScriptedWebSocket(
            "0{\"sid\":\"socket-id\",\"pingInterval\":25000,\"pingTimeout\":60000}",
            "40",
            "3");
        using var client = new DonationAlertsWidgetSocketClient(
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<script>new SocketIOClientProxy({ socketHost: 'wss://socket4.donationalerts.com:443' })</script>")
            })),
            (_, _) => Task.FromResult<WebSocket>(socket));
        var restored = 0;
        client.PlaybackChanged += (_, eventArgs) =>
        {
            if (eventArgs.Action == DonationAlertsPlaybackAction.ConnectionRestored)
            {
                Interlocked.Increment(ref restored);
            }
        };

        await client.PrepareAsync("WidgetToken_1234567890", TestContext.Current.CancellationToken);

        Assert.True(client.IsConnected);
        Assert.Equal(1, Volatile.Read(ref restored));
        Assert.DoesNotContain("40", socket.SentPackets);
        Assert.Contains("2", socket.SentPackets);
        Assert.Contains(socket.SentPackets, packet =>
            packet.StartsWith("42[\"add-user\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WidgetSocketRejectsANamespaceClosedBeforeReadiness()
    {
        var socket = new ScriptedWebSocket(
            "0{\"sid\":\"socket-id\",\"pingInterval\":25000,\"pingTimeout\":60000}",
            "40",
            "41");
        using var client = new DonationAlertsWidgetSocketClient(
            new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<script>new SocketIOClientProxy({ socketHost: 'wss://socket4.donationalerts.com:443' })</script>")
            })),
            (_, _) => Task.FromResult<WebSocket>(socket));

        await Assert.ThrowsAsync<IOException>(() =>
            client.PrepareAsync("WidgetToken_1234567890", TestContext.Current.CancellationToken));

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ObsControllerTriesEveryWidgetInOneSceneAndCachesOnlyTheStableCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-da-candidates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "STREAM.json"),
                """
                {"sources":[
                  {"name":"STALE","settings":{"url":"https://www.donationalerts.com/widget/alerts?token=StaleWidgetToken_123"}},
                  {"name":"DONATE","settings":{"url":"https://www.donationalerts.com/widget/alerts?token=GoodWidgetToken_4567"}}
                ]}
                """,
                TestContext.Current.CancellationToken);
            var socketClient = new CandidateWidgetSocketClient("StaleWidgetToken_123");
            var tokenStore = new MemoryTokenStore();
            using var controller = new DonationAlertsObsController(
                directory,
                socketClient,
                tokenStore,
                stableReadyDuration: TimeSpan.FromMilliseconds(20),
                initialReconnectDelay: TimeSpan.FromMilliseconds(5),
                readyWaitTimeout: TimeSpan.FromSeconds(1));

            await controller.EnsureConnectedAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(() =>
                Encoding.UTF8.GetString(tokenStore.Load() ?? []).Equals(
                    "GoodWidgetToken_4567",
                    StringComparison.Ordinal));

            Assert.Equal(
                ["StaleWidgetToken_123", "GoodWidgetToken_4567"],
                socketClient.PreparedTokens.Take(2));
            Assert.Equal("DONATE", controller.SourceName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ObsControllerConnectionWaitIsBoundedWhileBackgroundReconnectContinues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-da-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "STREAM.json"),
                "{\"sources\":[{\"settings\":{\"url\":\"https://www.donationalerts.com/widget/alerts?token=WidgetToken_1234567890\"}}]}",
                TestContext.Current.CancellationToken);
            var socketClient = new AlwaysFailingWidgetSocketClient();
            using var controller = new DonationAlertsObsController(
                directory,
                socketClient,
                new MemoryTokenStore(),
                stableReadyDuration: TimeSpan.FromMilliseconds(20),
                initialReconnectDelay: TimeSpan.FromMilliseconds(5),
                readyWaitTimeout: TimeSpan.FromMilliseconds(60));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                controller.EnsureConnectedAsync(TestContext.Current.CancellationToken));

            Assert.True(socketClient.PrepareCalls >= 2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RealtimeReconnectBackoffResetsOnlyAfterAStableConnection()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(2),
            DonationAlertsClient.AdvanceReconnectDelay(TimeSpan.FromSeconds(1), false));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            DonationAlertsClient.AdvanceReconnectDelay(TimeSpan.FromSeconds(30), false));
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            DonationAlertsClient.AdvanceReconnectDelay(TimeSpan.FromSeconds(16), true));
    }

    [Fact]
    public void RealtimeConnectionTreatsHttpTimeoutCancellationAsRecoverable()
    {
        Assert.True(DonationAlertsClient.IsTransientConnectionException(
            new TaskCanceledException("HTTP request timed out.")));
        Assert.True(DonationAlertsClient.IsTransientConnectionException(
            new TimeoutException("Transport timed out.")));
        Assert.False(DonationAlertsClient.IsTransientConnectionException(
            new ArgumentException("Programming error.")));
    }

    [Fact]
    public void WidgetSocketRequiresFullDonationPayloadBeforeConfirmingStart()
    {
        using var client = new DonationAlertsWidgetSocketClient(
            new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("No HTTP request expected.")));
        var events = new List<DonationAlertsPlaybackAction>();
        client.PlaybackChanged += (_, eventArgs) => events.Add(eventArgs.Action);

        client.ProcessSocketPacket(
            "42[\"alert-show\",{\"action\":\"start\",\"alert_id\":30530030,\"alert_type\":1}]");
        Assert.False(client.IsAlertPlaying);
        Assert.Empty(events);

        client.ProcessSocketPacket(
            "42[\"donation\",\"{\\\"id\\\":30530030,\\\"username\\\":\\\"Viewer\\\",\\\"amount\\\":500,\\\"currency\\\":\\\"RUB\\\"}\"]");
        Assert.True(client.IsAlertPlaying);
        Assert.Equal(30530030, client.ActiveAlertId);
        Assert.Equal([DonationAlertsPlaybackAction.Started], events);

        client.ProcessSocketPacket(
            "42[\"donation\",\"{\\\"id\\\":30530030,\\\"username\\\":\\\"Viewer\\\",\\\"amount\\\":500,\\\"currency\\\":\\\"RUB\\\"}\"]");

        client.ProcessSocketPacket(
            "42[\"alert-show\",{\"action\":\"skip\",\"alert_id\":30530030,\"alert_type\":1}]");
        Assert.False(client.IsAlertPlaying);
        Assert.Equal(
            [DonationAlertsPlaybackAction.Started, DonationAlertsPlaybackAction.Skipped],
            events);

        client.ProcessSocketPacket(
            "42[\"alert-show\",{\"action\":\"start\",\"alert_id\":30530030,\"alert_type\":1}]");
        Assert.False(client.IsAlertPlaying);
        Assert.Equal(
            [DonationAlertsPlaybackAction.Started, DonationAlertsPlaybackAction.Skipped],
            events);
    }

    [Fact]
    public void WidgetSocketDoesNotClearCurrentAlertForLateTerminalFromPreviousAlert()
    {
        using var client = new DonationAlertsWidgetSocketClient(
            new RecordingHandler(_ => throw new Xunit.Sdk.XunitException("No HTTP request expected.")));

        ConfirmSocketStart(client, 1001);
        ConfirmSocketStart(client, 1002);
        Assert.True(client.IsAlertPlaying);
        Assert.Equal(1002, client.ActiveAlertId);

        client.ProcessSocketPacket(
            "42[\"alert-show\",{\"action\":\"skip\",\"alert_id\":1001,\"alert_type\":1}]");

        Assert.True(client.IsAlertPlaying);
        Assert.Equal(1002, client.ActiveAlertId);

        client.ProcessSocketPacket(
            "42[\"alert-show\",{\"action\":\"skip\",\"alert_id\":1002,\"alert_type\":1}]");
        Assert.False(client.IsAlertPlaying);
        Assert.Equal(0, client.ActiveAlertId);
    }

    [Fact]
    public void RepeatResponseParserAcceptsJsonpAndRejectsErrors()
    {
        DonationAlertsWidgetSocketClient.ValidateRepeatResponse(
            "({\"status\":\"success\",\"message\":\"ok\"})");
        Assert.Throws<InvalidOperationException>(() =>
            DonationAlertsWidgetSocketClient.ValidateRepeatResponse(
                "{\"status\":\"error\",\"message\":\"rejected\"}"));
        Assert.Throws<InvalidDataException>(() =>
            DonationAlertsWidgetSocketClient.ValidateRepeatResponse("<html>blocked</html>"));
    }

    [Fact]
    public void TimedOutSkipOwnerReleasesItsWaiterSoTheNextCommandCanBeSent()
    {
        using var client = new DonationAlertsWidgetSocketClient(new RecordingHandler(_ =>
            Task.FromResult(Json("{}"))));
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(DonationAlertsWidgetSocketClient)
            .GetField("_skipWaiter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, waiter);
        typeof(DonationAlertsWidgetSocketClient)
            .GetField("_skipAlertId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, 42L);

        typeof(DonationAlertsWidgetSocketClient)
            .GetMethod("ReleaseSkipWaiter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [waiter, true]);

        Assert.Null(typeof(DonationAlertsWidgetSocketClient)
            .GetField("_skipWaiter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client));
        Assert.Equal(0L, (long)typeof(DonationAlertsWidgetSocketClient)
            .GetField("_skipAlertId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!);
    }

    [Fact]
    public async Task ObsControllerDoesNotSendCommandsForUntrustedWidgetUrls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-da-obs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "STREAM.json"),
                "{\"sources\":[{\"settings\":{\"url\":\"https://example.test/widget/alerts?token=stolen-token\"}}]}",
                TestContext.Current.CancellationToken);
            using var controller = new DonationAlertsObsController(
                directory,
                new RecordingWidgetSocketClient(),
                new MemoryTokenStore());
            var donation = new DonationAlert(
                "30530030", "Viewer", "Message", 500, "RUB", DateTimeOffset.UtcNow);

            Assert.False(controller.IsConfigured);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.RepeatDonationAsync(donation, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static void ConfirmSocketStart(DonationAlertsWidgetSocketClient client, long alertId)
    {
        client.ProcessSocketPacket(
            $"42[\"donation\",\"{{\\\"id\\\":{alertId},\\\"username\\\":\\\"Viewer\\\",\\\"amount\\\":1,\\\"currency\\\":\\\"RUB\\\"}}\"]");
        client.ProcessSocketPacket(
            $"42[\"alert-show\",{{\"action\":\"start\",\"alert_id\":{alertId},\"alert_type\":1}}]");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected DonationAlerts state was not reached.");
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }

    private sealed class BlockingSocketTokenHandler : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumConcurrentRequests;
        private int _requestCount;

        public int ActiveRequests => Volatile.Read(ref _activeRequests);
        public int MaximumConcurrentRequests => Volatile.Read(ref _maximumConcurrentRequests);
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _activeRequests);
            var maximum = Volatile.Read(ref _maximumConcurrentRequests);
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumConcurrentRequests,
                    active,
                    maximum);
                if (observed == maximum)
                {
                    break;
                }
                maximum = observed;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The request should only end through cancellation.");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }
    }

    private sealed class RecordingWidgetSocketClient : IDonationAlertsWidgetSocketClient
    {
        private int _prepareCalls;
        private int _startCalls;
        private int _prepareFailuresRemaining;
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;
        public string PreparedToken { get; private set; } = string.Empty;
        public string StartedToken { get; private set; } = string.Empty;
        public long StartedAlertId { get; private set; }
        public string SkippedToken { get; private set; } = string.Empty;
        public long SkippedAlertId { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsAlertPlaying { get; private set; }
        public long ActiveAlertId { get; private set; }
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public int StartCalls => Volatile.Read(ref _startCalls);
        public int PrepareFailuresRemaining
        {
            get => Volatile.Read(ref _prepareFailuresRemaining);
            set => Volatile.Write(ref _prepareFailuresRemaining, value);
        }

        public Task PrepareAsync(
            string widgetToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _prepareCalls);
            PreparedToken = widgetToken;
            if (Interlocked.Decrement(ref _prepareFailuresRemaining) >= 0)
            {
                throw new IOException("simulated connection failure");
            }
            IsConnected = true;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(
                    DonationAlertsPlaybackAction.ConnectionRestored,
                    0));
            return Task.CompletedTask;
        }

        public Task StartAlertAsync(
            string widgetToken,
            long alertId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCalls);
            StartedToken = widgetToken;
            StartedAlertId = alertId;
            IsAlertPlaying = true;
            ActiveAlertId = alertId;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(DonationAlertsPlaybackAction.Started, alertId));
            return Task.CompletedTask;
        }

        public void RaiseConnectionLost()
        {
            IsConnected = false;
            IsAlertPlaying = false;
            ActiveAlertId = 0;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(
                    DonationAlertsPlaybackAction.ConnectionLost,
                    0));
        }

        public Task SkipAlertAsync(
            string widgetToken,
            long alertId,
            CancellationToken cancellationToken = default)
        {
            SkippedToken = widgetToken;
            SkippedAlertId = alertId;
            IsAlertPlaying = false;
            ActiveAlertId = 0;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(DonationAlertsPlaybackAction.Skipped, alertId));
            return Task.CompletedTask;
        }
    }

    private sealed class CandidateWidgetSocketClient(string rejectedToken) : IDonationAlertsWidgetSocketClient
    {
        private readonly List<string> _preparedTokens = [];
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;
        public IReadOnlyList<string> PreparedTokens
        {
            get
            {
                lock (_preparedTokens)
                {
                    return _preparedTokens.ToArray();
                }
            }
        }
        public bool IsAlertPlaying => false;
        public bool IsConnected { get; private set; }
        public long ActiveAlertId => 0;

        public Task PrepareAsync(string widgetToken, CancellationToken cancellationToken = default)
        {
            lock (_preparedTokens)
            {
                _preparedTokens.Add(widgetToken);
            }
            if (string.Equals(widgetToken, rejectedToken, StringComparison.Ordinal))
            {
                throw new HttpRequestException(
                    "rejected token",
                    null,
                    HttpStatusCode.UnprocessableEntity);
            }
            IsConnected = true;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(DonationAlertsPlaybackAction.ConnectionRestored, 0));
            return Task.CompletedTask;
        }

        public Task StartAlertAsync(
            string widgetToken,
            long alertId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SkipAlertAsync(
            string widgetToken,
            long alertId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AlwaysFailingWidgetSocketClient : IDonationAlertsWidgetSocketClient
    {
        private int _prepareCalls;
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged
        {
            add { }
            remove { }
        }
        public int PrepareCalls => Volatile.Read(ref _prepareCalls);
        public bool IsAlertPlaying => false;
        public bool IsConnected => false;
        public long ActiveAlertId => 0;

        public Task PrepareAsync(string widgetToken, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _prepareCalls);
            return Task.FromException(new IOException("offline"));
        }

        public Task StartAlertAsync(
            string widgetToken,
            long alertId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SkipAlertAsync(
            string widgetToken,
            long alertId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ScriptedWebSocket(params string[] receivedPackets) : WebSocket
    {
        private readonly Queue<string> _receivedPackets = new(receivedPackets);
        private readonly List<string> _sentPackets = [];
        private WebSocketState _state = WebSocketState.Open;

        public IReadOnlyList<string> SentPackets
        {
            get
            {
                lock (_sentPackets)
                {
                    return _sentPackets.ToArray();
                }
            }
        }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            string packet;
            lock (_receivedPackets)
            {
                if (_receivedPackets.TryDequeue(out packet!))
                {
                    var bytes = Encoding.UTF8.GetBytes(packet);
                    Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, bytes.Length);
                    return new WebSocketReceiveResult(
                        bytes.Length,
                        WebSocketMessageType.Text,
                        endOfMessage: true);
                }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            lock (_sentPackets)
            {
                _sentPackets.Add(Encoding.UTF8.GetString(buffer));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryTokenStore : ISecureTokenStore
    {
        private byte[]? _data;
        public bool IsPersistent => true;
        public byte[]? Load() => _data?.ToArray();

        public bool TrySave(byte[] data)
        {
            _data = data.ToArray();
            return true;
        }

        public void Clear() => _data = null;
    }

    private sealed class BlockingTokenStore : ISecureTokenStore
    {
        private byte[]? _data;
        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsPersistent => true;
        public byte[]? Load() => _data?.ToArray();

        public bool TrySave(byte[] data)
        {
            SaveStarted.TrySetResult();
            ReleaseSave.Task.GetAwaiter().GetResult();
            _data = data.ToArray();
            return true;
        }

        public void Clear() => _data = null;
    }
}
