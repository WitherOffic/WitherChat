using System.Reflection;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("channel", false)]
    [InlineData("channel", true)]
    [InlineData("return-channel", false)]
    [InlineData("return-channel", true)]
    [InlineData("signout", false)]
    [InlineData("signout", true)]
    [InlineData("replacement", false)]
    [InlineData("replacement", true)]
    [InlineData("dispose", false)]
    [InlineData("dispose", true)]
    [InlineData("active", false)]
    [InlineData("active", true)]
    public async Task TwitchMessageModerationLateCompletionCannotChangeAnotherContext(
        string mode, bool fail)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        vm.CanModerate = true;
        var item = new ChatMessageItemViewModel(new ChatMessage
        {
            Id = "target", Channel = "channel_a", UserId = "viewer-id",
            UserLogin = "viewer", DisplayName = "Viewer", Text = "audit",
            Timestamp = DateTimeOffset.UtcNow
        }, fixture.ImageCache, vm.Texts, owner: vm);
        vm.Messages.Add(item);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<TwitchAuthSession, CancellationToken, Task> action = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            if (fail) throw new HttpRequestException("Synthetic old-context failure");
        };
        var method = typeof(MainWindowViewModel).GetMethod("RunModerationActionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task<bool>)method.Invoke(vm,
            [item, action, ChatMessageModerationState.Deleted, false])!;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (mode is "channel" or "return-channel")
            {
                vm.Channel = "channel_b";
                if (mode == "return-channel") vm.Channel = "channel_a";
            }
            else if (mode == "signout") vm.SignOutCommand.Execute(null);
            else if (mode == "dispose") await vm.DisposeAsync();
            else if (mode == "replacement")
            {
                var previous = (TwitchAuthSession)typeof(MainWindowViewModel)
                    .GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(vm)!;
                typeof(MainWindowViewModel).GetMethod("ApplyAuthenticatedSession",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm,
                    [previous with { UserId = "other-owner", Login = "other_owner" }]);
            }
            vm.StatusDetail = "current context";
            vm.ModerationPanelStatus = "current context";
            release.TrySetResult();
            var completed = await pending.WaitAsync(TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
            Assert.Equal(mode == "active" && !fail, completed);
            Assert.Equal(mode == "active" && !fail ? ChatMessageModerationState.Deleted :
                ChatMessageModerationState.None, item.ModerationState);
            if (mode != "active")
            {
                Assert.Equal("current context", vm.StatusDetail);
                Assert.Equal("current context", vm.ModerationPanelStatus);
            }
            else Assert.NotEqual("current context", vm.StatusDetail);
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
    }
}
