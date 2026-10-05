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
    [InlineData("held", "channel-return")]
    [InlineData("resolved", "channel-return")]
    [InlineData("ban", "channel-return")]
    [InlineData("unban", "channel-return")]
    [InlineData("request", "channel-return")]
    [InlineData("held", "sign-out")]
    [InlineData("resolved", "sign-out")]
    [InlineData("ban", "sign-out")]
    [InlineData("unban", "sign-out")]
    [InlineData("request", "sign-out")]
    [InlineData("held", "disposed")]
    [InlineData("resolved", "disposed")]
    [InlineData("ban", "disposed")]
    [InlineData("unban", "disposed")]
    [InlineData("request", "disposed")]
    [InlineData("held", "different-account")]
    [InlineData("resolved", "different-account")]
    [InlineData("ban", "different-account")]
    [InlineData("unban", "different-account")]
    [InlineData("request", "different-account")]
    public async Task QueuedTwitchModerationEventsCannotChangeANewerOrDisposedContext(string kind, string change)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        SeedRound12ModerationMarker(vm, kind);
        QueueRound12ModerationEvent(vm, kind);
        if (change == "disposed")
            await vm.DisposeAsync();
        else
        {
            if (change == "sign-out")
                vm.SignOutCommand.Execute(null);
            else if (change == "different-account")
            {
                Round9InvokeSession(vm, "ApplyAuthenticatedSession", new TwitchAuthSession(
                    "test-other-token", string.Empty, TwitchApplication.ClientId,
                    "other-owner-id", "other_owner", TwitchApplication.RequiredScopes,
                    DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow));
                vm.Channel = "channel_a";
            }
            else
            {
                vm.Channel = "channel_b";
                vm.Channel = "channel_a";
            }
            SeedRound12ModerationMarker(vm, kind);
        }
        await Round9PumpAsync();
        AssertRound12ModerationMarker(vm, kind);
    }

    [AvaloniaTheory]
    [InlineData("held")]
    [InlineData("resolved")]
    [InlineData("ban")]
    [InlineData("unban")]
    [InlineData("request")]
    public async Task LateTwitchModerationEventsAreIgnoredAfterSignOut(string kind)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        vm.SignOutCommand.Execute(null);
        vm.Channel = "channel_a";
        SeedRound12ModerationMarker(vm, kind);
        QueueRound12ModerationEvent(vm, kind);
        await Round9PumpAsync();
        AssertRound12ModerationMarker(vm, kind);
    }

    [AvaloniaFact]
    public async Task AutoModOfAnotherSavedChannelDoesNotAppearInTheActivePanel()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        Round9Invoke(vm, "OnAutoModMessageHeld",
            new AutoModHeldEventArgs(Round12Held("other") with
            { BroadcasterId = "channel_b", ChannelLogin = "channel_b" }));
        await Round9PumpAsync();
        Assert.Empty(vm.PendingAutoModMessages);
    }

    [AvaloniaFact]
    public async Task AutoModResolutionMustMatchTheBroadcasterAsWellAsMessageId()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        SeedRound12ModerationMarker(vm, "resolved");
        Round9Invoke(vm, "OnAutoModMessageResolved", new AutoModResolvedEventArgs("channel_b", "marker"));
        await Round9PumpAsync();
        AssertRound12ModerationMarker(vm, "resolved");
    }

    [AvaloniaTheory]
    [InlineData("held")]
    [InlineData("resolved")]
    [InlineData("ban")]
    [InlineData("unban")]
    [InlineData("request")]
    public async Task CurrentTwitchModerationEventsStillUpdateTheActivePanel(string kind)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.Channel = "channel_a";
        SeedRound12ModerationMarker(vm, kind);
        QueueRound12ModerationEvent(vm, kind);
        await Round9PumpAsync();
        switch (kind)
        {
            case "held":
                Assert.Equal(2, vm.PendingAutoModMessages.Count);
                break;
            case "resolved":
                Assert.Empty(vm.PendingAutoModMessages);
                break;
            case "ban":
                Assert.Equal("event", Assert.Single(vm.BannedUsers).Value.Reason);
                break;
            case "unban":
                Assert.Empty(vm.BannedUsers);
                break;
            case "request":
                Assert.Equal("event", Assert.Single(vm.UnbanRequests).Value.RequestText);
                break;
        }
    }

    private static void SeedRound12ModerationMarker(MainWindowViewModel vm, string kind)
    {
        if (kind is "held" or "resolved")
            vm.PendingAutoModMessages.Add(new HeldAutoModMessageViewModel(Round12Held("marker")));
        else if (kind is "ban" or "unban")
            vm.BannedUsers.Add(new BannedUserViewModel(new BannedUser(
                "42", "viewer", "Viewer", DateTimeOffset.UtcNow, null, "fresh")));
        else
            vm.UnbanRequests.Add(new UnbanRequestViewModel(new UnbanRequest(
                "request", "channel_a", "42", "viewer", "Viewer", "fresh",
                DateTimeOffset.UtcNow, UnbanRequestStatus.Pending, null, string.Empty), vm.Texts));
    }

    private static void AssertRound12ModerationMarker(MainWindowViewModel vm, string kind)
    {
        if (kind is "held" or "resolved")
            Assert.Equal("marker", Assert.Single(vm.PendingAutoModMessages).Value.MessageId);
        else if (kind is "ban" or "unban")
            Assert.Equal("fresh", Assert.Single(vm.BannedUsers).Value.Reason);
        else
            Assert.Equal("fresh", Assert.Single(vm.UnbanRequests).Value.RequestText);
    }

    private static void QueueRound12ModerationEvent(MainWindowViewModel vm, string kind)
    {
        switch (kind)
        {
            case "held":
                Round9Invoke(vm, "OnAutoModMessageHeld", new AutoModHeldEventArgs(Round12Held("event")));
                break;
            case "resolved":
                Round9Invoke(vm, "OnAutoModMessageResolved", new AutoModResolvedEventArgs("channel_a", "marker"));
                break;
            case "ban":
                Round9Invoke(vm, "OnEventSubUserBanned", new EventSubBanEventArgs(new EventSubBan(
                    "channel_a", "channel_a", "42", "viewer", "Viewer", "event",
                    DateTimeOffset.UtcNow, null, true)));
                break;
            case "unban":
                Round9Invoke(vm, "OnEventSubUserUnbanned",
                    new EventSubUnbanEventArgs(new EventSubUnban("channel_a", "channel_a", "42")));
                break;
            case "request":
                Round9Invoke(vm, "OnEventSubUnbanRequestChanged", new EventSubUnbanRequestEventArgs(
                    new EventSubUnbanRequest("request", "channel_a", "channel_a", "42", "viewer",
                        "Viewer", "event", DateTimeOffset.UtcNow, "pending")));
                break;
        }
    }

    private static HeldAutoModMessage Round12Held(string id) => new(id, "channel_a", "channel_a",
        "42", "viewer", "Viewer", "message", "category", 1, DateTimeOffset.UtcNow);
}
