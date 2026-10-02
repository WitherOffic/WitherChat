using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WitherChat.Desktop.Models;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task PendingRemovalDisablesOtherRemovalCommands()
    {
        var client = new NetworkAuditClient { BlockPart = true };
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        var first = new ChannelSessionViewModel("audit");
        var second = new ChannelSessionViewModel("beta");
        vm.SavedChannels.Clear();
        vm.SavedChannels.Add(first);
        vm.SavedChannels.Add(second);
        vm.SelectedSavedChannel = first;
        var pending = vm.RemoveSavedChannelCommand.ExecuteAsync(first);
        try
        {
            await client.PartStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(vm.IsChannelRemovalBusy);
            Assert.False(vm.RemoveSavedChannelCommand.CanExecute(second));
            Assert.False(vm.RemoveChannelCommand.CanExecute(null));
        }
        finally
        {
            client.Release.TrySetResult();
            await pending;
        }
        Assert.Same(second, Assert.Single(vm.SavedChannels));
        Assert.False(vm.IsChannelRemovalBusy);
        Assert.True(vm.RemoveSavedChannelCommand.CanExecute(second));
    }

    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(360, 400, "en", true)]
    public async Task ThreeChannelRowsAndFailureRemainReachableInSmallEditor(int width, int height, string language, bool light)
    {
        var client = new NetworkAuditClient { FailPart = true };
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        vm.SavedChannels.Clear();
        foreach (var login in new[] { "audit", "beta", "gamma" })
            vm.SavedChannels.Add(new ChannelSessionViewModel(login));
        vm.SelectedSavedChannel = vm.SavedChannels[0];
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        fixture.Window.Show();
        vm.IsChannelEditorOpen = true;
        await vm.RemoveSavedChannelCommand.ExecuteAsync(vm.SelectedSavedChannel);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var error = AssertControl<TextBlock>(fixture.Window, "ChannelOperationError");
        error.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        AssertAuxiliaryAuditInside(error, fixture.Window, width, height);
        var card = fixture.Window.FindControl<Border>("ChannelEditorCard")!;
        AssertAuxiliaryAuditInside(card, fixture.Window, width, height);
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"channel-three-error-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        var add = fixture.Window.FindControl<Button>("AddChannelButton")!;
        add.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        AssertAuxiliaryAuditInside(add, fixture.Window, width, height);
        var removeButtons = card.GetLogicalDescendants().OfType<Button>()
            .Where(button => ReferenceEquals(button.Command, vm.RemoveSavedChannelCommand)).ToArray();
        Assert.Equal(3, removeButtons.Length);
        foreach (var button in removeButtons)
        {
            button.BringIntoView();
            await SettleAuxiliaryAuditAsync(fixture.Window);
            AssertAuxiliaryAuditInside(button, fixture.Window, width, height);
            Assert.True(button.IsEnabled);
        }
    }
}
