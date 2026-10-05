using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task YouTubeModerationLifetimeAuditOnlyCurrentChannelCanBeModerated(bool foreign)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Round10ApplyYouTube(vm);
        vm.IsYouTubeLiveConnected = true;
        var item = Round10Item(fixture, foreign ? "UC-other" : "UC-owner");
        Assert.Equal(!foreign, vm.CanModerateTarget(item));
    }

    [AvaloniaFact]
    public async Task YouTubeModerationLifetimeAuditOldLoginRefreshCannotReplaceNewLoginToSameChannel()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var old = Round10ApplyYouTube(vm);
        var current = old with { AccessToken = "test-new-login", RefreshToken = "test-new-login-refresh" };
        Round9InvokeSession(vm, "ApplyYouTubeSession", current);
        var store = Round9Field<YouTubeAuthSessionStore>(vm, "_youTubeAuthSessionStore");
        store.Save(current);
        Round9Invoke(vm, "OnYouTubeSessionUpdated",
            new YouTubeSessionEventArgs(old with { AccessToken = "test-old-refresh" }, old));
        await Round9PumpAsync();
        Assert.Same(current, Round9Field<YouTubeAuthSession>(vm, "_youTubeAuthSession"));
        Assert.Equal(current.AccessToken, store.Load()!.AccessToken);
    }

    [AvaloniaFact]
    public async Task YouTubeModerationLifetimeAuditChainedCurrentRefreshesWithOriginAreStillSaved()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var first = Round10ApplyYouTube(vm);
        var second = first with { AccessToken = "test-refresh-second" };
        var third = second with { AccessToken = "test-refresh-third" };
        Round9Invoke(vm, "OnYouTubeSessionUpdated", new YouTubeSessionEventArgs(second, first));
        Round9Invoke(vm, "OnYouTubeSessionUpdated", new YouTubeSessionEventArgs(third, second));
        await Round9PumpAsync();
        Assert.Same(third, Round9Field<YouTubeAuthSession>(vm, "_youTubeAuthSession"));
        Assert.Equal(third.AccessToken, Round9Field<YouTubeAuthSessionStore>(vm, "_youTubeAuthSessionStore").Load()!.AccessToken);
    }

    [AvaloniaTheory]
    [InlineData("disconnect", "success")]
    [InlineData("disconnect", "cancel")]
    [InlineData("disconnect", "error")]
    [InlineData("replacement", "success")]
    [InlineData("replacement", "cancel")]
    [InlineData("replacement", "error")]
    [InlineData("dispose", "success")]
    [InlineData("dispose", "cancel")]
    [InlineData("dispose", "error")]
    [InlineData("active", "success")]
    [InlineData("active", "cancel")]
    [InlineData("active", "error")]
    public async Task YouTubeModerationLifetimeAuditLateCommandCompletionCannotChangeNewContext(string mode, string outcome)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Round10ApplyYouTube(vm);
        vm.IsYouTubeLiveConnected = true;
        var item = Round10Item(fixture);
        vm.Messages.Add(item);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task> action = async token =>
        {
            started.TrySetResult();
            await release.Task;
            if (outcome == "cancel") throw new OperationCanceledException(token);
            if (outcome == "error") throw new HttpRequestException("Synthetic timeout");
        };
        var method = typeof(MainWindowViewModel).GetMethod("RunYouTubeModerationActionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task<bool>)method.Invoke(vm, [item, action, ChatMessageModerationState.Deleted, false])!;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (mode == "disconnect") await vm.DisconnectYouTubeCommand.ExecuteAsync(null);
            else if (mode == "replacement") Round10ApplyYouTube(vm);
            else if (mode == "dispose") await vm.DisposeAsync();
            vm.StatusDetail = "current context";
            vm.ModerationPanelStatus = "current context";
            release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Null(error);
            Assert.Equal(mode == "active" && outcome == "success", await pending);
            Assert.Equal(mode == "active" && outcome == "success" ? ChatMessageModerationState.Deleted :
                ChatMessageModerationState.None, item.ModerationState);
            if (mode != "active")
            {
                Assert.Equal("current context", vm.StatusDetail);
                Assert.Equal("current context", vm.ModerationPanelStatus);
            }
            else if (outcome != "success")
                Assert.NotEqual("current context", vm.StatusDetail);
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    [AvaloniaTheory]
    [InlineData("delete")]
    [InlineData("ban")]
    [InlineData("unban")]
    public async Task YouTubeModerationLifetimeAuditDisconnectCancelsRealCommandWithoutFaultingUi(string action)
    {
        using var handler = new Round10YouTubeHandler(blockWrites: true);
        await using var fixture = new WindowFixture(youTubeHandler: handler);
        var vm = fixture.ViewModel;
        var session = Round10ApplyYouTube(vm);
        await fixture.YouTubeLiveChatClient.StartAsync(session);
        await WaitForAsync(() => vm.IsYouTubeLiveConnected, TimeSpan.FromSeconds(3));
        var item = Round10Item(fixture);
        vm.Messages.Add(item);
        Task pending;
        if (action == "delete") pending = vm.DeleteMessageCommand.ExecuteAsync(item);
        else if (action == "unban")
        {
            var ban = new YouTubeChatBanViewModel(new YouTubeChatBan("test-ban", "test-chat", "UC-viewer",
                "Viewer", DateTimeOffset.UtcNow, null), vm.Texts);
            vm.YouTubeBans.Add(ban);
            pending = vm.UnbanYouTubeUserCommand.ExecuteAsync(ban);
        }
        else
        {
            var method = typeof(MainWindowViewModel).GetMethod("RunYouTubeBanActionAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            pending = (Task<bool>)method.Invoke(vm, [item, 600, ChatMessageModerationState.TimedOut])!;
        }
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await vm.DisconnectYouTubeCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
            handler.Release.TrySetResult();
            Assert.Null(await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3))));
            Assert.Equal(ChatMessageModerationState.None, item.ModerationState);
            Assert.Empty(vm.YouTubeBans);
            Assert.False(vm.IsYouTubeConnected);
        }
        finally
        {
            handler.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    [AvaloniaFact]
    public async Task YouTubeModerationLifetimeAuditOldBroadcastBanCannotBeRemovedFromNewBroadcast()
    {
        using var handler = new Round10YouTubeHandler(blockWrites: false);
        await using var fixture = new WindowFixture(youTubeHandler: handler);
        var vm = fixture.ViewModel;
        await fixture.YouTubeLiveChatClient.StartAsync(Round10ApplyYouTube(vm));
        await WaitForAsync(() => vm.IsYouTubeLiveConnected, TimeSpan.FromSeconds(3));
        var old = new YouTubeChatBanViewModel(new YouTubeChatBan("old-ban", "old-chat", "UC-viewer",
            "Viewer", DateTimeOffset.UtcNow, null), vm.Texts);
        vm.YouTubeBans.Add(old);
        await vm.UnbanYouTubeUserCommand.ExecuteAsync(old);
        Assert.Equal(0, handler.Writes);
        Assert.Contains(old, vm.YouTubeBans);
    }

    private static YouTubeAuthSession Round10ApplyYouTube(MainWindowViewModel vm)
    {
        var session = new YouTubeAuthSession("test-access", "test-refresh", "client", "UC-owner", "Owner",
            "@owner", null, [YouTubeAuthService.ReadOnlyScope, YouTubeAuthService.ModerationScope],
            DateTimeOffset.UtcNow.AddHours(1));
        Round9InvokeSession(vm, "ApplyYouTubeSession", session);
        return session;
    }

    private static ChatMessageItemViewModel Round10Item(WindowFixture fixture, string channel = "UC-owner") =>
        new(new ChatMessage
        {
            Id = "test-message", Channel = "youtube_" + channel, BroadcasterId = channel,
            UserId = "UC-viewer", UserLogin = "viewer", DisplayName = "Viewer", Text = "Audit message",
            Timestamp = DateTimeOffset.UtcNow, Platform = ChatPlatforms.YouTube
        }, fixture.ImageCache, fixture.ViewModel.Texts, owner: fixture.ViewModel);

    private sealed class Round10YouTubeHandler(bool blockWrites) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        public int Writes => Volatile.Read(ref _writes);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body;
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref _writes);
                Started.TrySetResult();
                if (blockWrites) await Release.Task.WaitAsync(token);
                if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
                body = """{"id":"test-ban"}""";
            }
            else
                body = request.RequestUri!.AbsolutePath.EndsWith("liveBroadcasts", StringComparison.Ordinal)
                    ? """{"items":[{"id":"test-video","snippet":{"title":"Audit","liveChatId":"test-chat"}}]}"""
                    : """{"pollingIntervalMillis":30000,"items":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
