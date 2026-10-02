using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Models;
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
    public async Task LongMomentNotesKeepEditorAndActionsInsideViewport(
        int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        var item = new ChatMessageItemViewModel(new ChatMessage
        {
            Id = "audit-moment", Channel = "audit", UserLogin = "viewer", DisplayName = "Viewer",
            Text = string.Concat(Enumerable.Repeat("Длинное сообщение для сохранения момента. ", 12)),
            Timestamp = DateTimeOffset.UtcNow
        }, fixture.ImageCache, vm.Texts);
        var note = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Заметка {i}"));
        window.Show();
        vm.BeginSaveMomentCommand.Execute(item);
        vm.MomentNote = note;
        await SettleAuxiliaryAuditAsync(window);
        var card = window.FindControl<Border>("MomentEditorCard")!;
        var input = card.GetLogicalDescendants().OfType<TextBox>().Single();
        var save = card.GetLogicalDescendants().OfType<Button>().Single(
            button => ReferenceEquals(button.Command, vm.ConfirmSaveMomentCommand));
        var cancel = card.GetLogicalDescendants().OfType<Button>().Single(
            button => ReferenceEquals(button.Command, vm.CancelSaveMomentCommand));
        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"moment-editor-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        AssertAuxiliaryAuditInside(card, window, width, height);
        AssertAuxiliaryAuditInside(save, window, width, height);
        AssertAuxiliaryAuditInside(cancel, window, width, height);
        input.BringIntoView();
        await SettleAuxiliaryAuditAsync(window);
        Assert.True(input.Bounds.Height >= 60 && input.Bounds.Width >= 160);
        Assert.Equal(note, vm.MomentNote);
        ClickAtCenter(window, save);
        await WaitForAsync(() => vm.Moments.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(note, Assert.Single(vm.Moments).Note);
        Assert.False(vm.IsMomentEditorOpen);
        vm.BeginSaveMomentCommand.Execute(item);
        vm.MomentNote = note;
        await SettleAuxiliaryAuditAsync(window);
        ClickAtCenter(window, cancel);
        Assert.False(vm.IsMomentEditorOpen);
        Assert.Single(vm.Moments);
        Assert.Null(vm.MomentTarget);
    }

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
    public async Task ChannelSearchAndFollowedRowsStayReadableAndSelectableInCompactWindows(
        int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        var session = new TwitchAuthSession("audit-token", "", TwitchApplication.ClientId,
            "owner-id", "audit_owner",
            TwitchApplication.RequiredScopes.Concat([TwitchApplication.FollowedChannelsScope]).Distinct().ToArray(),
            DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        typeof(MainWindowViewModel).GetMethod("ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [session]);
        vm.IsConnectPanelOpen = true;
        window.Show();
        var rows = Enumerable.Range(1, 6).Select(index => new ChannelSearchResultViewModel(
            new ChannelSearchResult($"audit-{index}", $"very_long_channel_login_{index}",
                $"VeryLongDisplayName_Wither_{index}", "", "A very long game category name",
                "Audit channel", index % 2 == 0, DateTimeOffset.UtcNow, 123456), null)).ToArray();

        foreach (var followed in new[] { false, true })
        {
            vm.IsFollowedChannelsTabSelected = followed;
            var collection = followed ? vm.FollowedChannels : vm.ChannelSearchResults;
            collection.Clear();
            collection.AppendBatch(rows, rows.Length);
            typeof(ObservableObject).GetMethod("OnPropertyChanged",
                BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string)], null)!
                .Invoke(vm, [followed ? nameof(vm.HasFollowedChannels) : nameof(vm.HasChannelSearchResults)]);
            await SettleAuxiliaryAuditAsync(window);
            if (followed)
            {
                var input = window.FindControl<Border>("ConnectPanelCard")!.GetLogicalDescendants().OfType<TextBox>()
                    .Single(control => AutomationProperties.GetAutomationId(control) == "FollowedChannelsSearchInput");
                input.BringIntoView();
                await SettleAuxiliaryAuditAsync(window);
                Assert.True(input.Bounds.Width >= 120, $"Followed channel search input is squeezed: {input.Bounds}");
                AssertAuxiliaryAuditInside(input, window, width, height);
            }
            var card = window.FindControl<Border>("ConnectPanelCard")!;
            var buttons = card.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible && button.DataContext is ChannelSearchResultViewModel)
                .ToArray();
            Assert.Equal(rows.Length, buttons.Length);
            foreach (var button in new[] { buttons[0], buttons[^1] })
            {
                button.BringIntoView();
                await SettleAuxiliaryAuditAsync(window);
                var result = (ChannelSearchResultViewModel)button.DataContext!;
                var label = button.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == result.DisplayName);
                Assert.True(label.Bounds.Width >= 100,
                    $"Channel display name became unreadable: name={label.Bounds}, row={button.Bounds}");
                AssertAuxiliaryAuditInside(button, window, width, height);
                foreach (var text in button.GetVisualDescendants().OfType<TextBlock>()
                             .Where(text => text.IsEffectivelyVisible))
                {
                    var origin = text.TranslatePoint(default, button)!.Value;
                    Assert.True(origin.X >= 0 && origin.Y >= 0 &&
                                origin.X + text.Bounds.Width <= button.Bounds.Width + 0.5 &&
                                origin.Y + text.Bounds.Height <= button.Bounds.Height + 0.5,
                        $"Channel row text overflows: text={text.Text}, origin={origin}, bounds={text.Bounds}");
                }
                var clicked = false;
                button.Command = new RelayCommand(() => clicked = true);
                ClickAtCenter(window, button);
                Assert.True(clicked, "A channel row must receive a real pointer click.");
            }
            using var frame = window.CaptureRenderedFrame();
            SaveAuditFrame(frame!, $"channel-{(followed ? "followed" : "search")}-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
            foreach (var resizedWidth in new[] { 1100, 280, width })
            {
                window.Width = resizedWidth;
                window.Height = resizedWidth < 520 ? 400 : 760;
                vm.IsCompactMode = resizedWidth < 860;
                await SettleAuxiliaryAuditAsync(window);
                var firstRow = (Grid)buttons[0].Content!;
                Assert.Equal(resizedWidth < 520 ? 3 : 5, firstRow.ColumnDefinitions.Count);
                var display = buttons[0].GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == rows[0].DisplayName);
                Assert.True(display.Bounds.Width >= 100);
            }
            window.Height = height;
            await SettleAuxiliaryAuditAsync(window);
        }
    }

    private static void ConfigureAuxiliaryAuditWindow(
        WindowFixture fixture, int width, int height, string language, bool light)
    {
        fixture.Window.MinWidth = 280;
        fixture.Window.MinHeight = 340;
        fixture.Window.Width = width;
        fixture.Window.Height = height;
        fixture.Window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        fixture.ViewModel.ReduceMotion = true;
        fixture.ViewModel.Language = language;
        fixture.ViewModel.IsCompactMode = width < 860;
    }

    private static async Task SettleAuxiliaryAuditAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        using var initial = window.CaptureRenderedFrame();
        using var settled = window.CaptureRenderedFrame();
    }

    private static void AssertAuxiliaryAuditInside(Control control, Window window, int width, int height)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(origin.X >= 0 && origin.Y >= 42 &&
                    origin.X + control.Bounds.Width <= width + 0.5 &&
                    origin.Y + control.Bounds.Height <= height + 0.5,
            $"Control is clipped: name={control.Name}, origin={origin}, bounds={control.Bounds}, viewport={width}x{height}");
    }
}
