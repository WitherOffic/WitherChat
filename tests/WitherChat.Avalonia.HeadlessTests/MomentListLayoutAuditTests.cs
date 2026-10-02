using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
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
    public async Task LongMomentListRowsAndActionsRemainReadable(
        int width, int height, string language, bool light)
    {
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        var moments = Enumerable.Range(0, 20).Select(i => files.Moment with
        {
            Id = i.ToString(), Channel = "very_long_channel_name_for_layout_audit",
            User = "VeryLongViewerNameForLayoutAudit",
            Text = "Сообщение " + i + " " + new string('Ж', 160),
            Note = "Заметка " + i + " " + new string('Ю', 100)
        }).ToArray();
        await store.SaveAsync(moments);
        await using var fixture = new WindowFixture(streamMomentStore: store);
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        window.Show();
        vm.OpenMomentsPanelCommand.Execute(null);
        await SettleAuxiliaryAuditAsync(window);
        var card = window.FindControl<Border>("MomentsPanelCard")!;
        var export = window.FindControl<Button>("ExportMomentsButton")!;
        var close = card.GetLogicalDescendants().OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button) == "CloseMomentsButton");
        using var initial = window.CaptureRenderedFrame();
        SaveAuditFrame(initial!, $"moments-before-scroll-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        AssertAuxiliaryAuditInside(card, window, width, height);
        AssertAuxiliaryAuditInside(export, window, width, height);
        AssertAuxiliaryAuditInside(close, window, width, height);
        var list = card.GetLogicalDescendants().OfType<ListBox>().Single();
        Assert.True(list.Bounds.Width >= 180 && list.Bounds.Height >= 60);
        list.ScrollIntoView(vm.Moments[0]);
        await SettleAuxiliaryAuditAsync(window);
        var row = list.GetVisualDescendants().OfType<Border>().Single(border =>
            border.DataContext is StreamMomentItemViewModel item && item.Value.Id == vm.Moments[0].Value.Id &&
            border.Child is Grid grid && grid.Children.Count == 5);
        var identity = (Grid)((Grid)row.Child!).Children[0];
        var user = identity.Children.OfType<TextBlock>().Single(text => text.Text == moments[0].User);
        var channel = identity.Children.OfType<TextBlock>().Single(text => text.Text == "@" + moments[0].Channel);
        Assert.True(user.Bounds.Width >= 100 && channel.Bounds.Width >= 80,
            $"Moment identity squeezed: user={user.Bounds}, channel={channel.Bounds}");
        var delete = row.GetLogicalDescendants().OfType<Button>().Single();
        delete.BringIntoView();
        await SettleAuxiliaryAuditAsync(window);
        AssertAuxiliaryAuditInside(delete, window, width, height);
        ClickAtCenter(window, delete);
        await WaitForAsync(() => vm.Moments.Count == 19, TimeSpan.FromSeconds(2));
        Assert.Equal(19, store.Load().Count);
        Assert.DoesNotContain(vm.Moments, item => item.Value.Id == moments[0].Id);
        list.ScrollIntoView(vm.Moments[^1]);
        await SettleAuxiliaryAuditAsync(window);
        var lastDelete = list.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is StreamMomentItemViewModel item && ReferenceEquals(item, vm.Moments[^1]));
        lastDelete.BringIntoView();
        await SettleAuxiliaryAuditAsync(window);
        AssertAuxiliaryAuditInside(lastDelete, window, width, height);
        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"moments-last-row-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        export.Focus();
        for (var index = 0; index < 12; index++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            var focused = Assert.IsAssignableFrom<Control>(
                TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
            Assert.Contains(focused.GetVisualAncestors(), ancestor => ReferenceEquals(ancestor, card));
        }
        ClickAtCenter(window, close);
        Assert.False(vm.IsMomentsPanelOpen);
    }
}
