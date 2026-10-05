using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using Xunit;
namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedChannelAvatarShowsInitialUntilItsBitmapIsReady(bool assignResource)
    {
        await using var fixture = new WindowFixture();
        var channel = new ChannelSessionViewModel("viewer") { DisplayName = "Viewer" };
        if (assignResource) channel.ProfileImageResource = new ChatImageResource(34);
        fixture.ViewModel.SavedChannels.Clear();
        fixture.ViewModel.SavedChannels.Add(channel);
        fixture.ViewModel.IsChannelEditorOpen = true;
        fixture.Window.Show();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var card = fixture.Window.FindControl<Border>("ChannelEditorCard")!;
        var initial = Assert.Single(card.GetLogicalDescendants().OfType<TextBlock>(),
            block => block.DataContext == channel && block.Text == channel.AvatarInitial);
        Assert.True(initial.IsEffectivelyVisible, "Loading or failed channel avatar lost its initial fallback.");
        if (channel.ProfileImageResource is { } resource)
        {
            using var media = ChatImageDecoder.Decode(Convert.FromBase64String(
                "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
                "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7"));
            resource.SetMedia(media!, 34);
            await SettleAuxiliaryAuditAsync(fixture.Window);
            var image = Assert.Single(card.GetLogicalDescendants().OfType<Image>(),
                control => control.DataContext == channel);
            Assert.True(image.IsEffectivelyVisible);
            Assert.Same(resource.Image, image.Source);
            resource.Media = null;
            await SettleAuxiliaryAuditAsync(fixture.Window);
            Assert.True(initial.IsEffectivelyVisible);
            Assert.False(image.IsEffectivelyVisible);
        }
    }
}
