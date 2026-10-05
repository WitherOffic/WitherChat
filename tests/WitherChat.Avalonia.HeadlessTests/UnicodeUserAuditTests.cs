using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("\U0001F600User", "\U0001F600")]
    [InlineData("e\u0301clair", "E\u0301")]
    [InlineData("\U0001F469\u200D\U0001F4BB coder", "\U0001F469\u200D\U0001F4BB")]
    [InlineData("\U0001F1F7\U0001F1FA viewer", "\U0001F1F7\U0001F1FA")]
    [InlineData("  alice", "A")]
    public async Task UnicodeUserAuditAvatarFallbackKeepsEntireFirstGrapheme(string displayName, string expected)
    {
        var message = new ChatMessage
        {
            Id = "unicode", Channel = "audit", UserLogin = "viewer",
            DisplayName = displayName, Text = "hello", Timestamp = DateTimeOffset.UtcNow,
            Platform = ChatPlatforms.YouTube
        };
        Assert.Equal(expected, message.AvatarInitial);
        var channel = new WitherChat.Desktop.Models.ChannelSessionViewModel("audit") { DisplayName = displayName };
        Assert.Equal(expected, channel.AvatarInitial);
        await using var fixture = new WindowFixture();
        ApplyAuditSession(fixture.ViewModel);
        fixture.ViewModel.AccountDisplayName = displayName;
        Assert.Equal(expected, fixture.ViewModel.AvatarInitial);
    }
}
