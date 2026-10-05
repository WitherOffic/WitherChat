using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class YouTubeLifetimeAuditTests
{
    private const string ClientId = "123456789012-example.apps.googleusercontent.com";

    [Theory]
    [InlineData("delete")]
    [InlineData("unban")]
    public async Task StoppedClientForgetsSessionAndCannotSendModeration(string action)
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        await client.StopAsync();
        Assert.Null(SessionField.GetValue(client));
        Assert.Equal("youtube_UC-owner", client.CurrentChannel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModerateAsync(client, action));
        Assert.Empty(api.Writes);
    }

    [Theory]
    [InlineData("stop", "delete")]
    [InlineData("stop", "unban")]
    [InlineData("stop", "ban")]
    [InlineData("dispose", "delete")]
    [InlineData("dispose", "unban")]
    [InlineData("dispose", "ban")]
    [InlineData("restart", "delete")]
    [InlineData("restart", "unban")]
    [InlineData("restart", "ban")]
    public async Task ConnectionChangeCancelsModerationRefreshWithoutSendingOldAction(string mode, string action)
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        var session = (YouTubeAuthSession)SessionField.GetValue(client)!;
        SessionField.SetValue(client, session with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var updatedSessions = new ConcurrentQueue<YouTubeAuthSession>();
        client.SessionUpdated += (_, args) => updatedSessions.Enqueue(args.Session);
        var pending = ModerateAsync(client, action);
        try
        {
            await authHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (mode == "stop") await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            else if (mode == "dispose") await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            else await client.StartAsync(Session("UC-new"), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            authHandler.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Empty(api.Writes);
            Assert.Empty(updatedSessions);
            Assert.True(authHandler.RequestToken.IsCancellationRequested);
            if (mode == "restart")
            {
                await WaitConnectedAsync(client);
                Assert.Equal("youtube_UC-new", client.CurrentChannel);
                Assert.Equal("UC-new", ((YouTubeAuthSession)SessionField.GetValue(client)!).ChannelId);
            }
            else
            {
                Assert.Null(SessionField.GetValue(client));
                Assert.False(client.IsConnected);
            }
        }
        finally
        {
            authHandler.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("ban")]
    public async Task CurrentAccountCannotModerateMessagesFromOldChannel(string action)
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client, "UC-new");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ModerateAsync(client, action));
        Assert.Empty(api.Writes);
    }

    [Fact]
    public async Task StopAndRepeatedDisposeRemainSafeAfterDisposal()
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        await client.DisposeAsync();
        await client.StopAsync();
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.StartAsync(Session(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("unban")]
    [InlineData("ban")]
    public async Task CurrentConnectionStillAllowsModeration(string action)
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        await ModerateAsync(client, action);
        Assert.Single(api.Writes);
        Assert.Equal("test-access-UC-owner", api.Writes.Single());
    }

    [Fact]
    public async Task CallerCancellationStopsRefreshWithoutStoppingCurrentConnection()
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        var session = (YouTubeAuthSession)SessionField.GetValue(client)!;
        SessionField.SetValue(client, session with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.RemoveBanAsync("test-ban", cancellation.Token);
        try
        {
            await authHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            Assert.Empty(api.Writes);
            Assert.True(client.IsConnected);
            SessionField.SetValue(client, session);
            await client.RemoveBanAsync("test-ban", TestContext.Current.CancellationToken);
            Assert.Single(api.Writes);
        }
        finally { authHandler.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("restart")]
    public async Task ConnectionChangeDrainsActiveWriteAndCancelsQueuedActions(string mode)
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler(blockWrites: true);
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        var first = ModerateAsync(client, "unban");
        await api.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var queued = new[] { first, ModerateAsync(client, "delete"), ModerateAsync(client, "ban") };
        try
        {
            Assert.Single(api.Writes);
            if (mode == "dispose") await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            else if (mode == "restart") await client.StartAsync(Session("UC-new"), TestContext.Current.CancellationToken);
            else await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            foreach (var pending in queued)
                Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(async () =>
                    await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)));
            Assert.Single(api.Writes);
            if (mode == "restart") { await WaitConnectedAsync(client); Assert.Equal("youtube_UC-new", client.CurrentChannel); }
        }
        finally
        {
            api.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await Task.WhenAll(queued).WaitAsync(
                TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SuccessfulRefreshIncludesTheOriginatingSession()
    {
        using var authHandler = new DelayedRefreshHandler();
        using var auth = CreateAuth(authHandler);
        using var api = new AuditYouTubeHandler();
        await using var client = new YouTubeLiveChatClient(auth, api);
        await StartConnectedAsync(client);
        var session = ((YouTubeAuthSession)SessionField.GetValue(client)!) with
        {
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        SessionField.SetValue(client, session);
        YouTubeSessionEventArgs? update = null;
        client.SessionUpdated += (_, args) => update = args;
        authHandler.Release.TrySetResult();
        await ModerateAsync(client, "unban");
        Assert.NotNull(update);
        Assert.Same(session, update.PreviousSession);
        Assert.Equal("test-refreshed-old", update.Session.AccessToken);
        Assert.Single(api.Writes);
    }

    private static readonly FieldInfo SessionField =
        typeof(YouTubeLiveChatClient).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static YouTubeAuthService CreateAuth(HttpMessageHandler handler)
    {
        var service = new YouTubeAuthService(handler);
        service.Configure(ClientId);
        return service;
    }

    private static YouTubeAuthSession Session(string channel = "UC-owner") =>
        new("test-access-" + channel, "test-refresh", ClientId, channel, "Owner", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope, YouTubeAuthService.ModerationScope],
            DateTimeOffset.UtcNow.AddHours(1));

    private static ChatMessage Message() => new()
    {
        Id = "test-message", PlatformMessageId = "test-platform-message", Channel = "youtube_UC-owner",
        BroadcasterId = "UC-owner", UserId = "UC-viewer", UserLogin = "viewer", DisplayName = "Viewer",
        Text = "Audit", Timestamp = DateTimeOffset.UtcNow, Platform = ChatPlatforms.YouTube
    };

    private static Task ModerateAsync(YouTubeLiveChatClient client, string action) => action switch
    {
        "delete" => client.DeleteMessageAsync(Message(), TestContext.Current.CancellationToken),
        "unban" => client.RemoveBanAsync("test-ban", TestContext.Current.CancellationToken),
        "ban" => client.BanUserAsync(Message(), 600, TestContext.Current.CancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static async Task StartConnectedAsync(YouTubeLiveChatClient client, string channel = "UC-owner")
    {
        await client.StartAsync(Session(channel), TestContext.Current.CancellationToken);
        await WaitConnectedAsync(client);
    }

    private static async Task WaitConnectedAsync(YouTubeLiveChatClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!client.IsConnected && DateTimeOffset.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(client.IsConnected);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class DelayedRefreshHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post)
            {
                RequestToken = token;
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                return Json("""{"access_token":"test-refreshed-old","expires_in":3600}""");
            }
            return Json("""{"items":[{"id":"UC-owner","snippet":{"title":"Owner","customUrl":"@owner"}}]}""");
        }
    }

    private sealed class AuditYouTubeHandler(bool blockWrites = false) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Writes { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get)
            {
                Writes.Enqueue(request.Headers.Authorization!.Parameter!);
                Started.TrySetResult();
                if (blockWrites) await Release.Task.WaitAsync(token);
                return request.Method == HttpMethod.Post
                    ? Json("""{"id":"test-ban"}""")
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal)
                ? Json("""{"items":[{"id":"test-video","snippet":{"title":"Audit","liveChatId":"test-chat"}}]}""")
                : Json("""{"pollingIntervalMillis":30000,"items":[]}""");
        }
    }
}
