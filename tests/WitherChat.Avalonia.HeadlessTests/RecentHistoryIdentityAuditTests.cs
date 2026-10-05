using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(ChatPlatforms.Twitch, ChatPlatforms.YouTube)]
    [InlineData(ChatPlatforms.YouTube, ChatPlatforms.Twitch)]
    public async Task RecentHistoryDoesNotMixPlatformsWithIdenticalNicknames(
        string targetPlatform, string otherPlatform)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ChatMessageItemViewModel Create(string id, string platform, string userId) => new(
            new ChatMessage
            {
                Id = id, Channel = "audit", UserLogin = "same_name",
                UserId = userId, DisplayName = "Same Name", Text = id,
                Timestamp = DateTimeOffset.UtcNow, Platform = platform
            }, fixture.ImageCache, vm.Texts, owner: vm);
        var target = Create("target", targetPlatform, "target-user");
        var unrelated = Create("other", otherPlatform, "other-user");
        vm.Messages.AppendBatch([target, unrelated], 100);

        vm.ShowRecentMessagesCommand.Execute(target);

        Assert.Equal(["target"], vm.RecentUserMessages.Select(item => item.Value.Id).ToArray());
    }

    [AvaloniaTheory]
    [InlineData(ChatPlatforms.Twitch)]
    [InlineData(ChatPlatforms.YouTube)]
    public async Task RecentHistoryUsesKnownIdsInsteadOfReusedNicknames(string platform)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ChatMessageItemViewModel Create(string id, string userId, string login) => new(
            new ChatMessage
            {
                Id = id, Channel = "audit", UserLogin = login,
                UserId = userId, DisplayName = login, Text = id,
                Timestamp = DateTimeOffset.UtcNow, Platform = platform
            }, fixture.ImageCache, vm.Texts, owner: vm);
        var older = Create("old-name", "target-user", "previous_name");
        var target = Create("current", "target-user", "new_name");
        var reusedNickname = Create("different-user", "another-user", "new_name");
        vm.Messages.AppendBatch([older, target, reusedNickname], 100);

        vm.ShowRecentMessagesCommand.Execute(target);

        Assert.Equal(["old-name", "current"],
            vm.RecentUserMessages.Select(item => item.Value.Id).ToArray());
    }

    [AvaloniaFact]
    public async Task RecentHistoryEmptyLoginDoesNotIncludeUnrelatedKnownUsers()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ChatMessageItemViewModel Create(string id, string userId) => new(
            new ChatMessage
            {
                Id = id, Channel = "audit", UserLogin = string.Empty,
                UserId = userId, DisplayName = id, Text = id,
                Timestamp = DateTimeOffset.UtcNow
            }, fixture.ImageCache, vm.Texts, owner: vm);
        var target = Create("target", "target-user");
        var unrelated = Create("other", "other-user");
        vm.Messages.AppendBatch([target, unrelated], 100);
        vm.ShowRecentMessagesCommand.Execute(target);

        Assert.Equal(["target"], vm.RecentUserMessages.Select(item => item.Value.Id).ToArray());
    }

    [AvaloniaFact]
    public async Task RecentHistoryFallsBackToCaseInsensitiveLoginForAnonymousHistory()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ChatMessageItemViewModel Create(string id, string userId, string login) => new(
            new ChatMessage
            {
                Id = id, Channel = "audit", UserLogin = login,
                UserId = userId, DisplayName = login, Text = id,
                Timestamp = DateTimeOffset.UtcNow
            }, fixture.ImageCache, vm.Texts, owner: vm);
        var older = Create("anonymous-history", string.Empty, "VIEWER");
        var target = Create("known-user", "viewer-id", "viewer");
        vm.Messages.AppendBatch([older, target], 100);
        vm.ShowRecentMessagesCommand.Execute(target);

        Assert.Equal(["anonymous-history", "known-user"],
            vm.RecentUserMessages.Select(item => item.Value.Id).ToArray());
    }
}
