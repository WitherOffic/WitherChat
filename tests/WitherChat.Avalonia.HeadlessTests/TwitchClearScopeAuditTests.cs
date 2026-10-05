using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("active", false)]
    [InlineData("channel", false)]
    [InlineData("return-channel", false)]
    [InlineData("signout", false)]
    [InlineData("channel", true)]
    public async Task TwitchClearConfirmationOnlyChangesItsOwnCurrentChannel(string mode, bool fail)
    {
        using var handler = new ScopedClearAuditHandler(fail);
        await using var fixture = new WindowFixture(apiHandler: handler);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        Round10ApplyYouTube(vm);
        vm.Channel = "channel_a";
        vm.CanModerate = true;
        ChatMessageItemViewModel Create(string id, string channel, string platform) => new(
            new ChatMessage
            {
                Id = id, Channel = channel, UserLogin = "viewer", UserId = "viewer-id",
                DisplayName = "Viewer", Text = id, Timestamp = DateTimeOffset.UtcNow.AddSeconds(-5),
                Platform = platform
            }, fixture.ImageCache, vm.Texts, owner: vm);
        var target = Create("target", "channel_a", ChatPlatforms.Twitch);
        var other = Create("other-twitch", "channel_b", ChatPlatforms.Twitch);
        var youtube = Create("youtube", "youtube_UC-owner", ChatPlatforms.YouTube);
        vm.Messages.AppendBatch([target, other, youtube], 100);
        vm.VisibleMessages.AppendBatch([target, other, youtube], 100);
        vm.TwitchVisibleMessages.AppendBatch([target, other], 100);
        vm.YouTubeVisibleMessages.AppendBatch([youtube], 100);
        vm.RequestClearChatCommand.Execute(null);
        var pending = vm.ConfirmClearChatCommand.ExecuteAsync(null);
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            var fresh = new ChatMessageItemViewModel(target.Message with
            {
                Id = "fresh-after-admission", Text = "fresh", Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(1)
            }, fixture.ImageCache, vm.Texts, owner: vm);
            vm.Messages.Add(fresh);
            if (mode is "channel" or "return-channel")
            {
                vm.Channel = "channel_b";
                if (mode == "return-channel") vm.Channel = "channel_a";
            }
            else if (mode == "signout") vm.SignOutCommand.Execute(null);
            vm.ProtectionStatus = "current context";
            handler.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Contains(fresh, vm.Messages);
            Assert.Contains(other, vm.Messages);
            Assert.Contains(youtube, vm.Messages);
            Assert.Contains(youtube, vm.YouTubeVisibleMessages);
            if (mode == "active")
            {
                Assert.DoesNotContain(target, vm.Messages);
                Assert.NotEqual("current context", vm.ProtectionStatus);
            }
            else
            {
                Assert.Contains(target, vm.Messages);
                Assert.Equal("current context", vm.ProtectionStatus);
            }
            Assert.False(vm.IsProtectionBusy);
        }
        finally
        {
            handler.Release.TrySetResult();
            _ = await Record.ExceptionAsync(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
    }

    private sealed class ScopedClearAuditHandler(bool fail) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                if (fail) throw new HttpRequestException("Old clear request failed");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"channel-a-id","login":"channel_a","display_name":"Channel A","profile_image_url":""}]}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
