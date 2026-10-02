using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task MomentOrderSurvivesRestartWhenOlderMessageIsSavedLater()
    {
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        var recentMessage = files.Moment with { Id = "recent-message",
            MessageTimestamp = DateTimeOffset.UtcNow, SavedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var latestSave = files.Moment with { Id = "latest-save",
            MessageTimestamp = DateTimeOffset.UtcNow.AddDays(-1), SavedAtUtc = DateTimeOffset.UtcNow };
        await store.SaveAsync([latestSave, recentMessage]);
        await using var fixture = new WindowFixture(streamMomentStore: store);
        Assert.Equal(new[] { "latest-save", "recent-message" },
            fixture.ViewModel.GetMomentSnapshot().Select(value => value.Id));
    }

    [AvaloniaTheory]
    [InlineData(280, 340, "LanguageComboBox", "ru", false)]
    [InlineData(280, 340, "ThemeComboBox", "ru", false)]
    [InlineData(280, 340, "FontComboBox", "ru", false)]
    [InlineData(360, 400, "LanguageComboBox", "en", true)]
    [InlineData(360, 400, "ThemeComboBox", "en", true)]
    [InlineData(1100, 760, "LanguageComboBox", "ru", false)]
    [InlineData(1100, 760, "ThemeComboBox", "ru", false)]
    [InlineData(1100, 760, "FontComboBox", "ru", false)]
    public async Task EscapeClosesDropdownBeforeSettingsPanel(
        int width, int height, string controlId, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        fixture.Window.Show();
        fixture.ViewModel.IsSettingsOpen = true;
        fixture.ViewModel.SelectedSettingsSection = WitherChat.Desktop.ViewModels.SettingsSection.Program;
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var combo = AssertControl<ComboBox>(fixture.Window, controlId);
        combo.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.True(combo.Focus());
        var selection = combo.SelectedItem;
        ClickAtCenter(fixture.Window, combo);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.True(combo.IsDropDownOpen);
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.False(combo.IsDropDownOpen);
        Assert.True(fixture.ViewModel.IsSettingsOpen);
        Assert.True(combo.IsFocused);
        Assert.Equal(selection, combo.SelectedItem);
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"dropdown-escape-{controlId}-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        fixture.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.False(fixture.ViewModel.IsSettingsOpen);
    }
}
