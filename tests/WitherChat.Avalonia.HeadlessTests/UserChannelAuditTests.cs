using Avalonia.Headless.XUnit;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(" @AuDiT ", "audit")]
    [InlineData("#AuDiT", "audit")]
    [InlineData("audit", "audit")]
    public async Task UserChannelAuditCopiedLoginJoinsCanonicalChannel(string input, string expected)
    {
        var client = new NetworkAuditClient(joined: false);
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        vm.SavedChannels.Clear();
        vm.Channel = input;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(expected, vm.Channel);
        Assert.Equal(expected, Assert.Single(client.Channels));
        Assert.Equal(expected, Assert.Single(vm.SavedChannels).Login);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Single(vm.SavedChannels);
        Assert.Single(client.Channels);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("@")]
    [InlineData("not a login")]
    [InlineData("https://twitch.tv/audit")]
    [InlineData("audit\r\nJOIN #other")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    public async Task UserChannelAuditInvalidInputNeverJoinsOrCreatesChannel(string input)
    {
        var client = new NetworkAuditClient(joined: false);
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        vm.SavedChannels.Clear();
        vm.Channel = input;
        Assert.False(vm.ConnectCommand.CanExecute(null));
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Empty(client.Channels);
        Assert.Empty(vm.SavedChannels);
    }
}
