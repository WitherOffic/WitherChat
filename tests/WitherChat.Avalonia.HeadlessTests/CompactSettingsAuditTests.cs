using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
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
    [InlineData(860, 560, "ru", false)]
    [InlineData(860, 560, "ru", true)]
    [InlineData(860, 560, "en", false)]
    [InlineData(860, 560, "en", true)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(1100, 760, "ru", true)]
    [InlineData(1100, 760, "en", false)]
    [InlineData(1100, 760, "en", true)]
    public async Task SettingsSectionsStayReadableAndActionsReachableAtEveryWindowSize(
        int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        window.Show();
        vm.IsSettingsOpen = true;
        await SettleAuxiliaryAuditAsync(window);
        await WaitForAsync(() => window.FindControl<Border>("SettingsOverlay")!.Opacity > 0.99,
            TimeSpan.FromSeconds(2));
        var card = window.FindControl<Border>("SettingsCard")!;
        var scroll = window.FindControl<ScrollViewer>("SettingsContentScrollViewer")!;
        var save = card.GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "SaveSettingsButton");
        var cancel = card.GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "CancelSettingsButton");
        using var initial = window.CaptureRenderedFrame();
        SaveAuditFrame(initial!, $"settings-initial-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        var cardOrigin = card.TranslatePoint(default, window)!.Value;
        Assert.True(cardOrigin.X >= 0 && cardOrigin.Y >= (width < 740 ? 42 : 0) &&
                    cardOrigin.X + card.Bounds.Width <= width + 0.5 &&
                    cardOrigin.Y + card.Bounds.Height <= height + 0.5,
            $"Settings card is clipped: origin={cardOrigin}, bounds={card.Bounds}");
        AssertAuxiliaryAuditInside(save, window, width, height);
        AssertAuxiliaryAuditInside(cancel, window, width, height);
        Assert.True(scroll.Bounds.Width >= 180 && scroll.Bounds.Height >= 40,
            $"Settings have no usable content viewport: {scroll.Bounds}");
        foreach (var section in Enum.GetValues<SettingsSection>())
        {
            vm.SelectedSettingsSection = section;
            await SettleAuxiliaryAuditAsync(window);
            var sectionCard = scroll.GetLogicalDescendants().OfType<Border>()
                .Single(border => border.IsEffectivelyVisible && border.Name == section + "SettingsCard");
            var controls = sectionCard.GetLogicalDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible &&
                    control is Button or TextBox or ComboBox or CheckBox or RadioButton)
                .ToArray();
            foreach (var control in controls)
            {
                control.BringIntoView();
                await SettleAuxiliaryAuditAsync(window);
                AssertAuxiliaryAuditInside(control, window, width, height);
                Assert.True(control.Bounds.Width >= (control is ComboBox ? 90 : control is TextBox ? 40 : 20),
                    $"Unusable control: type={control.GetType().Name}, id={AutomationProperties.GetAutomationId(control)}, bounds={control.Bounds}");
                if (control is Button wallet && wallet.Classes.Contains("support-wallet"))
                {
                    var address = wallet.GetLogicalDescendants().OfType<TextBlock>()
                        .Single(text => text.TextTrimming == global::Avalonia.Media.TextTrimming.CharacterEllipsis);
                    Assert.Equal(global::Avalonia.Media.TextWrapping.NoWrap, address.TextWrapping);
                    Assert.True(address.Bounds.Width >= (width < 740 ? 100 : 200));
                    foreach (var text in wallet.GetLogicalDescendants().OfType<TextBlock>())
                    {
                        var origin = text.TranslatePoint(default, wallet)!.Value;
                        Assert.True(origin.X >= 0 && origin.Y >= 0 &&
                            origin.X + text.Bounds.Width <= wallet.Bounds.Width + 0.5 &&
                            origin.Y + text.Bounds.Height <= wallet.Bounds.Height + 0.5,
                            $"Wallet content is clipped: {text.Text}, origin={origin}, bounds={text.Bounds}");
                    }
                }
            }
            using var frame = window.CaptureRenderedFrame();
            SaveAuditFrame(frame!, $"settings-{section}-{language}-{(light ? "light" : "dark")}-{width}x{height}.png");
        }
        ClickAtCenter(window, cancel);
        Assert.False(vm.IsSettingsOpen);
        vm.IsSettingsOpen = true;
        await SettleAuxiliaryAuditAsync(window);
        window.KeyPress(global::Avalonia.Input.Key.Escape, global::Avalonia.Input.RawInputModifiers.None, global::Avalonia.Input.PhysicalKey.Escape, null);
        window.KeyRelease(global::Avalonia.Input.Key.Escape, global::Avalonia.Input.RawInputModifiers.None, global::Avalonia.Input.PhysicalKey.Escape, null);
        Assert.False(vm.IsSettingsOpen);
    }

    [AvaloniaTheory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task SettingsLayoutAndSectionChoiceSurviveRepeatedResizes(string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        ConfigureAuxiliaryAuditWindow(fixture, 1100, 760, language, light);
        window.Show();
        vm.IsSettingsOpen = true;
        await SettleAuxiliaryAuditAsync(window);
        var scroll = window.FindControl<ScrollViewer>("SettingsContentScrollViewer")!;
        var selector = window.FindControl<ComboBox>("CompactSettingsSectionSelector")!;
        var original = DescribeSettingsLayout(scroll);
        foreach (var width in new[] { 280, 360, 280 })
        {
            window.Width = width;
            window.Height = 400;
            await SettleAuxiliaryAuditAsync(window);
            Assert.True(selector.IsVisible);
            foreach (var section in Enum.GetValues<SettingsSection>())
            {
                selector.SelectedIndex = (int)section;
                await SettleAuxiliaryAuditAsync(window);
                Assert.Equal(section, vm.SelectedSettingsSection);
                Assert.Equal(default, scroll.Offset);
            }
            window.Width = 1100;
            window.Height = 760;
            await SettleAuxiliaryAuditAsync(window);
            Assert.False(selector.IsVisible);
            Assert.Equal(original, DescribeSettingsLayout(scroll));
        }
        vm.SelectedSettingsSection = SettingsSection.Program;
        await SettleAuxiliaryAuditAsync(window);
        var save = window.FindControl<Border>("SettingsCard")!.GetLogicalDescendants()
            .OfType<Button>().Single(button =>
                AutomationProperties.GetAutomationId(button) == "SaveSettingsButton");
        ClickAtCenter(window, save);
        await WaitForAsync(() => !vm.IsSettingsOpen, TimeSpan.FromSeconds(2));
        Assert.False(vm.IsSettingsOpen);
    }

    private static string[] DescribeSettingsLayout(ScrollViewer scroll) =>
        scroll.GetLogicalDescendants().OfType<Control>().Select(control => control switch
        {
            Grid grid => "grid:" + string.Join(",", grid.ColumnDefinitions.Select(c => c.Width)) +
                ";" + string.Join(",", grid.RowDefinitions.Select(r => r.Height)) +
                ";" + grid.ColumnSpacing + ";" + string.Join("|", grid.Children.Select(child =>
                    $"{Grid.GetRow(child)},{Grid.GetColumn(child)},{Grid.GetRowSpan(child)},{Grid.GetColumnSpan(child)}")),
            global::Avalonia.Controls.Primitives.UniformGrid grid => $"uniform:{grid.Columns},{grid.Rows}",
            StackPanel panel => "stack:" + panel.Orientation,
            Button button => "button:" + button.Height,
            _ => null
        }).Where(value => value is not null).Select(value => value!).ToArray();
}
