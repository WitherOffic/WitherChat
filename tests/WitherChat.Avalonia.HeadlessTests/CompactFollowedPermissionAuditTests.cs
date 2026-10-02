using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(280, 340, "ru", true)]
    [InlineData(280, 340, "en", false)]
    [InlineData(280, 340, "en", true)]
    [InlineData(360, 400, "ru", false)]
    [InlineData(360, 400, "ru", true)]
    [InlineData(360, 400, "en", false)]
    [InlineData(360, 400, "en", true)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(1100, 760, "ru", true)]
    [InlineData(1100, 760, "en", false)]
    [InlineData(1100, 760, "en", true)]
    public async Task FollowedPermissionRequestKeepsDescriptionAndActionReadable(
        int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        var session = new TwitchAuthSession("audit-token", "", TwitchApplication.ClientId,
            "owner-id", "audit_owner",
            TwitchApplication.RequiredScopes.Where(scope => scope != TwitchApplication.FollowedChannelsScope).ToArray(),
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        typeof(MainWindowViewModel).GetMethod("ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [session]);
        vm.IsFollowedChannelsTabSelected = true;
        vm.IsConnectPanelOpen = true;
        window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var card = window.FindControl<Border>("ConnectPanelCard")!.GetLogicalDescendants().OfType<Border>()
            .Single(border => AutomationProperties.GetAutomationId(border) == "FollowedChannelsPermissionCard");
        Assert.True(card.IsEffectivelyVisible);
        var description = card.GetLogicalDescendants().OfType<TextBlock>()
            .Single(text => text.Text == vm.Texts.FollowedChannelsPermissionRequired);
        var action = card.GetLogicalDescendants().OfType<Button>().Single();
        action.BringIntoView();
        await SettleAuxiliaryAuditAsync(window);
        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"followed-permission-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        Assert.True(description.Bounds.Width >= 160,
            $"Permission description is squeezed by its button: {description.Bounds}");
        AssertAuxiliaryAuditInside(action, window, width, height);
        var clicked = false;
        action.Command = new RelayCommand(() => clicked = true);
        ClickAtCenter(window, action);
        Assert.True(clicked);
        Assert.False(vm.IsAuthorizing);
    }
}
