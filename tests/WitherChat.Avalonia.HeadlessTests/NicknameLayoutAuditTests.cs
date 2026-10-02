using System.Reflection;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(360, 400, false)]
    [InlineData(360, 400, true)]
    [InlineData(1100, 760, false)]
    [InlineData(1100, 760, true)]
    public async Task NicknamesStayVisibleWhenSwitchingThemesWithExistingMessages(int width, int height, bool light)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, "ru", !light);
        vm.Channel = "audit";
        var colors = new[] { "", "#FFFFFF", "#FFFF00", "#00FF00", "#000000", "#0000FF" };
        foreach (var color in colors)
            vm.Messages.Add(new ChatMessageItemViewModel(NicknameMessage(color),
                fixture.ImageCache, vm.Texts, owner: vm));
        typeof(MainWindowViewModel).GetMethod("RebuildVisibleMessages",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        fixture.Window.Show();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        vm.Theme = light ? "Light" : "Dark";
        fixture.Window.RequestedThemeVariant = light ? global::Avalonia.Styling.ThemeVariant.Light : global::Avalonia.Styling.ThemeVariant.Dark;
        await SettleAuxiliaryAuditAsync(fixture.Window);
        foreach (var item in vm.Messages)
        {
            var fresh = new ChatMessageItemViewModel(item.Message, fixture.ImageCache, vm.Texts, owner: vm);
            Assert.Equal(BrushColor(fresh.UserBrush), BrushColor(item.UserBrush));
            Assert.True(item.HasText);
        }
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"nickname-switch-{(light ? "light" : "dark")}-{width}x{height}.png");
    }

    [AvaloniaFact]
    public async Task DeferredNicknameUsesCurrentThemeWhenFollowingResumes()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.Channel = "audit";
        vm.Theme = "Dark";
        vm.SetFollowingLatest(false);
        var item = new ChatMessageItemViewModel(NicknameMessage("#FFFFFF"),
            fixture.ImageCache, vm.Texts, owner: vm);
        var deferred = (List<ChatMessageItemViewModel>)typeof(MainWindowViewModel)
            .GetField("_deferredMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        deferred.Add(item);
        vm.Theme = "Light";
        vm.SetFollowingLatest(true);
        var expected = new ChatMessageItemViewModel(item.Message, fixture.ImageCache, vm.Texts, owner: vm);
        Assert.Same(item, Assert.Single(vm.VisibleMessages));
        Assert.Equal(BrushColor(expected.UserBrush), BrushColor(item.UserBrush));
    }

    [AvaloniaFact]
    public async Task RecentUserSnapshotOutsideLiveHistoryUsesCurrentTheme()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.Theme = "Dark";
        var item = new ChatMessageItemViewModel(NicknameMessage("#FFFFFF"),
            fixture.ImageCache, vm.Texts, owner: vm);
        vm.RecentUserMessages.Add(new RecentUserMessageViewModel(item));
        Assert.Empty(vm.Messages);
        vm.Theme = "Light";
        var expected = new ChatMessageItemViewModel(item.Message, fixture.ImageCache, vm.Texts, owner: vm);
        Assert.Equal(BrushColor(expected.UserBrush), BrushColor(item.UserBrush));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LogAndLiveNicknamesUseTheSameThemeColor(bool light)
    {
        using var cache = new ChatImageCache();
        var entry = ChatLogEntryViewModel.Parse(
            """{"displayName":"Viewer","color":"#FFFFFF","text":"message"}""");
        entry.ApplyPresentation(cache, null, null, true, true, true, useLightTwitchTheme: light);
        var live = new ChatMessageItemViewModel(NicknameMessage("#FFFFFF"), cache, new UiText());
        live.ApplyPresentationSettings(true, true, true, null, light);
        Assert.Equal(BrushColor(live.UserBrush), BrushColor(entry.UserBrush!));
        Assert.Equal("#FFFFFF", entry.UserColor);
    }

    [AvaloniaTheory]
    [InlineData("#00FF7F", false)]
    [InlineData("#0000FF", true)]
    public void AlreadyReadableTwitchColorsArePreserved(string color, bool light)
    {
        using var cache = new ChatImageCache();
        var item = new ChatMessageItemViewModel(NicknameMessage(color), cache, new UiText());
        item.ApplyPresentationSettings(true, true, true, null, light);
        Assert.Equal(Color.Parse(color), BrushColor(item.UserBrush));
    }

    private static Color BrushColor(IBrush brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    private static ChatMessage NicknameMessage(string color) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Channel = "audit", UserLogin = "viewer",
        DisplayName = color.Length == 0 ? "Default" : "Viewer " + color,
        Text = "Ник должен оставаться читаемым 👋", UserColor = color, Timestamp = DateTimeOffset.UtcNow
    };
}
