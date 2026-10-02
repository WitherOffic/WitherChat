using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using WitherChat.Core.Models;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(360, 400, "en", true)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(1100, 760, "en", true)]
    public async Task MessageBurstsPreserveReadableChatAndPanelActions(int width, int height, string language, bool light)
    {
        var client = new NetworkAuditClient();
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        vm.SavedChannels.Clear();
        var channel = new ChannelSessionViewModel("audit");
        vm.SavedChannels.Add(channel);
        vm.SelectedSavedChannel = channel;
        vm.Channel = "audit";
        vm.MessageLimit = 250;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        fixture.Window.Show();
        PublishRange(0, 400);
        Drain();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        await vm.OpenModerationPanelCommand.ExecuteAsync(null);
        vm.SetFollowingLatest(false);
        PublishRange(400, 1200);
        Drain();
        Assert.True(vm.UnreadCount > 0);
        vm.SetFollowingLatest(true);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var close = AssertControl<Button>(fixture.Window, "CloseModerationButton");
        AssertAuxiliaryAuditInside(close, fixture.Window, width, height);
        ClickAtCenter(fixture.Window, close);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.False(vm.IsModerationPanelOpen);
        Assert.InRange(vm.Messages.Count, 1, 750);
        Assert.InRange(vm.VisibleMessages.Count, 1, 250);
        Assert.All(vm.VisibleMessages, item => Assert.Equal("audit", item.Message.Channel));
        Assert.Equal("burst-1199", vm.VisibleMessages.Last().Message.Id);
        foreach (var compact in new[] { !vm.IsCompactMode, vm.IsCompactMode })
        {
            vm.IsCompactMode = compact;
            fixture.Window.Width = compact ? 360 : 1100;
            fixture.Window.Height = compact ? 400 : 760;
            await SettleAuxiliaryAuditAsync(fixture.Window);
            var list = fixture.Window.FindControl<ListBox>("MessagesList")!;
            list.ScrollIntoView(vm.VisibleMessages.Last());
            await SettleAuxiliaryAuditAsync(fixture.Window);
            var texts = list.GetVisualDescendants().OfType<RichChatTextBlock>()
                .Where(control => control.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(texts);
            Assert.All(texts, text =>
            {
                Assert.True(text.Bounds.Width > 0);
                Assert.True(text.TextLayout.Width > 0);
            });
        }
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"burst-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");

        void PublishRange(int start, int end)
        {
            for (var index = start; index < end; index++)
                client.Publish(new ChatMessage { Id = "burst-" + index,
                    Channel = index % 3 == 0 ? "beta" : "audit",
                    UserLogin = "viewer" + index % 10, DisplayName = "Viewer" + index % 10,
                    Text = "Сообщение потока " + index + " — hello 👋",
                    Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(index) });
        }
        void Drain()
        {
            var pending = typeof(MainWindowViewModel).GetField("_pendingMessageCount",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var batch = 0; batch < 2000 && (int)pending.GetValue(vm)! > 0; batch++)
                vm.ProcessPendingMessageBatch();
            Assert.Equal(0, (int)pending.GetValue(vm)!);
        }
    }
}
