using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru")]
    [InlineData(280, 340, "en")]
    [InlineData(1100, 760, "ru")]
    [InlineData(1100, 760, "en")]
    public async Task NestedMomentEditorKeepsTabFocusAndEscapeReturnsToParent(
        int width, int height, string language)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, false);
        window.Show();
        await SettleAuxiliaryAuditAsync(window);
        var opener = window.FindControl<Button>("CompactModeButton")!;
        Assert.True(opener.Focus());
        vm.OpenMomentsPanelCommand.Execute(null);
        await SettleAuxiliaryAuditAsync(window);
        var help = window.FindControl<Button>("MomentsHelpButton")!;
        Assert.True(help.Focus());
        vm.BeginSaveMomentCommand.Execute(MomentAuditMessage(fixture));
        vm.MomentNote = "draft";
        await SettleAuxiliaryAuditAsync(window);
        var editor = window.FindControl<Border>("MomentEditorCard")!;
        for (var index = 0; index < 12; index++)
        {
            var modifiers = index < 6 ? RawInputModifiers.None : RawInputModifiers.Shift;
            window.KeyPress(Key.Tab, modifiers, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, modifiers, PhysicalKey.Tab, null);
            var focused = Assert.IsAssignableFrom<Control>(
                TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
            Assert.Contains(focused.GetVisualAncestors(), ancestor => ReferenceEquals(ancestor, editor));
        }
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAuxiliaryAuditAsync(window);
        Assert.False(vm.IsMomentEditorOpen);
        Assert.True(vm.IsMomentsPanelOpen);
        Assert.True(help.IsFocused);
        Assert.Empty(vm.MomentNote);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAuxiliaryAuditAsync(window);
        Assert.False(vm.IsMomentsPanelOpen);
        Assert.True(opener.IsFocused);
    }
}
