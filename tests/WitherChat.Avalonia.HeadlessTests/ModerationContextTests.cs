using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WitherChat.Core.Models;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OldModerationRefreshCannotPopulateAnotherChannel(bool fail, bool returnToOriginal)
    {
        var handler = new DelayedModerationAuditHandler("banned", fail);
        await using var fixture = new WindowFixture(apiHandler: handler);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        vm.CanModerate = true;
        var refresh = vm.RefreshModerationPanelCommand.ExecuteAsync(null);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.Channel = "channel_b";
        if (returnToOriginal) vm.Channel = "channel_a";
        vm.ModerationPanelStatus = "new context";
        handler.Release.TrySetResult();
        await refresh;
        Assert.Empty(vm.BannedUsers);
        Assert.Empty(vm.UnbanRequests);
        Assert.Equal("new context", vm.ModerationPanelStatus);
        Assert.False(vm.IsModerationPanelBusy);
        Assert.DoesNotContain(handler.Requests, request => request.Contains("unban_requests", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task OldModerationPermissionsCannotEnableAnotherChannel()
    {
        var handler = new DelayedModerationAuditHandler("access");
        await using var fixture = new WindowFixture(apiHandler: handler);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        var method = typeof(MainWindowViewModel).GetMethod("RefreshModerationAccessSafeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var refresh = (Task)method.Invoke(vm, null)!;
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.Channel = "channel_b";
        handler.Release.TrySetResult();
        await refresh;
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        Assert.False(vm.CanModerate);
    }

    [AvaloniaFact]
    public async Task ChannelChangeImmediatelyInvalidatesModerationState()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        vm.CanModerate = true;
        vm.IsModerationPanelBusy = true;
        vm.IsModerationDialogOpen = true;
        vm.ModerationPanelStatus = "old error";
        vm.PendingAutoModMessages.Add(AuditHeldMessage());
        vm.Channel = "channel_b";
        Assert.False(vm.CanModerate);
        Assert.False(vm.IsModerationPanelBusy);
        Assert.False(vm.IsModerationDialogOpen);
        Assert.Empty(vm.PendingAutoModMessages);
        Assert.Empty(vm.ModerationPanelStatus);
    }

    [AvaloniaFact]
    public async Task RemovedModerationRowsCannotSendActionsInAnotherChannel()
    {
        var handler = new DelayedModerationAuditHandler("none");
        await using var fixture = new WindowFixture(apiHandler: handler);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        var banned = new BannedUserViewModel(new BannedUser("viewer-id", "viewer", "Viewer",
            DateTimeOffset.UtcNow, null, string.Empty));
        var request = AuditUnbanRequest(UnbanRequestStatus.Pending);
        vm.BannedUsers.Add(banned);
        vm.UnbanRequests.Add(request);
        vm.Channel = "channel_b";
        vm.CanModerate = true;
        await vm.UnbanListedUserCommand.ExecuteAsync(banned);
        await vm.ApproveUnbanRequestCommand.ExecuteAsync(request);
        Assert.DoesNotContain(handler.Requests, request => !request.StartsWith("GET", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task UnbanByLoginStopsBeforeSendingWhenChannelChangesDuringLookup()
    {
        var handler = new DelayedModerationAuditHandler("users");
        await using var fixture = new WindowFixture(apiHandler: handler);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        vm.CanModerate = true;
        vm.ModerationUserLogin = "viewer";
        var unban = vm.UnbanByLoginCommand.ExecuteAsync(null);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.Channel = "channel_b";
        vm.ModerationPanelStatus = "new context";
        handler.Release.TrySetResult();
        await unban;
        Assert.DoesNotContain(handler.Requests, request => request.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Equal("new context", vm.ModerationPanelStatus);
        Assert.Equal("viewer", vm.ModerationUserLogin);
    }

    private sealed class DelayedModerationAuditHandler(string delayTarget, bool fail = false) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Requests.Add(request.Method + " " + uri.PathAndQuery);
            var path = uri.AbsolutePath;
            var login = uri.Query.Contains("login=viewer", StringComparison.Ordinal) ? "viewer"
                : uri.Query.Contains("login=channel_b", StringComparison.Ordinal) ? "channel_b" : "channel_a";
            var delayed = delayTarget == "banned" && path.EndsWith("/moderation/banned", StringComparison.Ordinal)
                || delayTarget == "access" && path.EndsWith("/moderation/channels", StringComparison.Ordinal)
                || delayTarget == "users" && path.EndsWith("/users", StringComparison.Ordinal) && login == "viewer";
            if (delayed)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                if (fail) throw new HttpRequestException("Old channel failure");
            }
            if (path.EndsWith("/users", StringComparison.Ordinal))
                return JsonResponse(new { data = new[] { new { id = login, login, display_name = login, profile_image_url = "" } } });
            if (path.EndsWith("/moderation/channels", StringComparison.Ordinal))
                return JsonResponse(new { data = new[] { new { broadcaster_id = "channel_a", broadcaster_login = "channel_a", broadcaster_name = "Channel A" } }, pagination = new { } });
            if (path.EndsWith("/moderation/banned", StringComparison.Ordinal))
                return JsonResponse(new { data = new[] { new { user_id = "old-viewer", user_login = "old_viewer", user_name = "Old viewer", created_at = "2026-10-01T00:00:00Z", expires_at = "", reason = "" } }, pagination = new { } });
            return JsonResponse(new { data = Array.Empty<object>(), pagination = new { } });
        }

        private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
