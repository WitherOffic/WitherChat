
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public async Task ObsVisualPanelsMatchStandalonePixelForPixel(string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window; var vm = fixture.ViewModel;
        vm.Language = language; vm.ReduceMotion = true;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.MinWidth = window.MinHeight = 280;
        window.Show();
        var panels = new Dictionary<string, string>
        {
            ["IsSettingsOpen"] = "SettingsCard", ["IsConnectPanelOpen"] = "ConnectPanelCard",
            ["IsLogViewerOpen"] = "LogViewerCard", ["IsStreamEventsOpen"] = "StreamEventsCard",
            ["IsProtectionPanelOpen"] = "ProtectionPanelCard", ["IsMomentsPanelOpen"] = "MomentsPanelCard",
            ["IsModerationPanelOpen"] = "ModerationPanelCard", ["IsRecentMessagesOpen"] = "RecentMessagesCard",
            ["IsModerationDialogOpen"] = "ModerationDialogCard", ["IsClearChatConfirmationOpen"] = "ClearChatConfirmationCard"
        };
        foreach (var (width, dockHeight) in new[] { (280, 340), (360, 400), (600, 600), (1100, 718) })
        {
            // Compare panels with equal available space, not the differently sized host captions.
            // Desktop caption: 42px; the compact OBS caption is 36px at every width.
            var normalHeight = dockHeight + 42 - 36;
            window.MinWidth = window.MinHeight = 280;
            vm.IsCompactMode = width < 860;
            window.Width = width; window.Height = normalHeight;
            await SettleAuxiliaryAuditAsync(window);
            foreach (var (flag, name) in panels)
            {
                var property = vm.GetType().GetProperty(flag)!;
                property.SetValue(vm, true);
                await SettleAuxiliaryAuditAsync(window);
                var card = window.FindControl<Border>(name)!;
                var standalone = RenderPanelForParity(card);
                var beforeState = DescribeParityPanel(card);
                using (var frame = window.CaptureRenderedFrame()) SaveAuditFrame(frame!, $"obs-r3-full-reference-{name}-{language}-{light}-{width}x{dockHeight}.png");
                window.ConfigureObsDockLayout(true);
                window.Width = width; window.Height = dockHeight;
                await SettleAuxiliaryAuditAsync(window);
                var embedded = RenderPanelForParity(card);
                var afterState = DescribeParityPanel(card);
                using (var frame = window.CaptureRenderedFrame()) SaveAuditFrame(frame!, $"obs-r3-full-embedded-{name}-{language}-{light}-{width}x{dockHeight}.png");
                SaveAuditFrame(standalone, $"obs-r3-reference-{name}-{language}-{light}-{width}x{dockHeight}.png");
                SaveAuditFrame(embedded, $"obs-r3-embedded-{name}-{language}-{light}-{width}x{dockHeight}.png");
                try
                {
                    Assert.Equal(standalone.PixelSize, embedded.PixelSize);
                    using var first = new MemoryStream(); using var second = new MemoryStream();
                    standalone.Save(first, PngBitmapEncoderOptions.Default); embedded.Save(second, PngBitmapEncoderOptions.Default);
                    Assert.True(first.ToArray().AsSpan().SequenceEqual(second.ToArray()),
                        $"Different rendering: {name} {language}/{light} {width}x{dockHeight}; " +
                        beforeState + "\nAFTER\n" + afterState);
                    Assert.False(window.FindControl<Border>("ObsDockToolbar")!.IsEnabled);
                    var header = card.Child is Grid grid ? grid.Children.FirstOrDefault() as Grid : null;
                    if (header is not null) AssertHeaderControlsDoNotOverlap(header, name);
                    if (header is not null && width < 600)
                    {
                        var title = header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                        if (title is not null) Assert.Equal(TextTrimming.None, title.TextTrimming);
                    }
                }
                finally { standalone.Dispose(); embedded.Dispose(); }
                property.SetValue(vm, false);
                window.ConfigureObsDockLayout(false);
                window.MinWidth = window.MinHeight = 280;
                window.Width = width; window.Height = normalHeight;
                await SettleAuxiliaryAuditAsync(window);
            }
        }
    }


    [AvaloniaTheory]
    [InlineData("ru", false, 0)] [InlineData("ru", true, 0)]
    [InlineData("en", false, 0)] [InlineData("en", true, 0)]
    [InlineData("ru", false, 1)] [InlineData("ru", true, 1)]
    [InlineData("en", false, 1)] [InlineData("en", true, 1)]
    [InlineData("ru", false, 2)] [InlineData("ru", true, 2)]
    [InlineData("en", false, 2)] [InlineData("en", true, 2)]
    public async Task ObsVisualDonationsMatchStandalonePixelForPixel(string language, bool light, int scenario)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.Language = language; vm.ReduceMotion = true;
        if (scenario > 0) ApplyDonationAlertsSessionForTesting(vm);
        if (scenario == 2)
        {
            WitherChat.Core.Models.DonationAlert[] donations =
            [
                new("visual-1", "VeryLongDonorNickname", "Спасибо за стрим! Thank you for the stream and the chat.", 1234.56m, "RUB", DateTimeOffset.UtcNow),
                new("visual-2", "Viewer", "Audio message", 10m, "USD", DateTimeOffset.UtcNow, MessageType: "audio")
            ];
            vm.GetType().GetMethod("ApplyDonationHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(vm, [donations]);
        }
        var window = new DonationAlertsWindow { DataContext = vm,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        try
        {
            window.MinWidth = window.MinHeight = 280; window.Show();
            foreach (var (width, height) in new[] { (280, 280), (360, 400), (520, 620), (900, 760) })
            {
                window.MinWidth = window.MinHeight = 280;
                window.Width = width; window.Height = height;
                await SettleAuxiliaryAuditAsync(window);
                var content = window.FindControl<Grid>("DonationContentRoot")!;
                using var standalone = RenderPanelForParity(content);
                ClickAtCenter(window, AssertControl<Button>(window, "DonationHelpButton"));
                await SettleAuxiliaryAuditAsync(window);
                Assert.True(vm.ShowDonationTutorial);
                using var standaloneGuide = RenderPanelForParity(window.FindControl<Border>("DonationTutorialCard")!);
                window.ConfigureObsDockLayout(true);
                window.Width = width; window.Height = height;
                await SettleAuxiliaryAuditAsync(window);
                using var embeddedGuide = RenderPanelForParity(window.FindControl<Border>("DonationTutorialCard")!);
                AssertVisualParity(standaloneGuide, embeddedGuide, $"Donation guide {language}/{light}/{scenario} {width}x{height}");
                vm.SkipOnboardingCommand.Execute(null);
                await SettleAuxiliaryAuditAsync(window);
                using var embedded = RenderPanelForParity(content);
                SaveAuditFrame(standalone, $"obs-r3-donation-reference-{language}-{light}-{scenario}-{width}x{height}.png");
                SaveAuditFrame(embedded, $"obs-r3-donation-embedded-{language}-{light}-{scenario}-{width}x{height}.png");
                using (var frame = window.CaptureRenderedFrame())
                    SaveAuditFrame(frame!, $"obs-r3-donation-full-{language}-{light}-{scenario}-{width}x{height}.png");
                AssertVisualParity(standalone, embedded, $"Donations {language}/{light}/{scenario} {width}x{height}");
                window.ConfigureObsDockLayout(false);
                window.MinWidth = window.MinHeight = 280;
            }
        }
        finally { vm.SkipOnboardingCommand.Execute(null); window.DataContext = null; window.CloseForApplicationExit(); }
    }

    private static void AssertVisualParity(RenderTargetBitmap first, RenderTargetBitmap second, string description)
    {
        Assert.Equal(first.PixelSize, second.PixelSize);
        using var left = new MemoryStream(); using var right = new MemoryStream();
        first.Save(left, PngBitmapEncoderOptions.Default); second.Save(right, PngBitmapEncoderOptions.Default);
        Assert.True(left.ToArray().AsSpan().SequenceEqual(right.ToArray()), $"Different rendering: {description}");
    }

    private static void AssertHeaderControlsDoNotOverlap(Grid header, string description)
    {
        var controls = header.GetVisualDescendants().OfType<Button>().Cast<Control>()
            .Concat(header.Children.OfType<StackPanel>()).Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            .ToArray();
        for (var first = 0; first < controls.Length; first++)
        for (var second = first + 1; second < controls.Length; second++)
        {
            var left = new Rect(controls[first].TranslatePoint(default, header)!.Value, controls[first].Bounds.Size);
            var right = new Rect(controls[second].TranslatePoint(default, header)!.Value, controls[second].Bounds.Size);
            var overlap = left.Intersect(right);
            Assert.True(overlap.Width <= .5 || overlap.Height <= .5,
                $"Overlapping header controls: {description}, {controls[first].Name}/{controls[second].Name}, {left}/{right}");
        }
    }

    private static string DescribeParityPanel(Control panel) => string.Join("\n",
        panel.GetVisualDescendants().Where(control => control is Button or TextBlock).Select(control =>
            control is Button button ? $"BUTTON {button.Name} {button.Content} {button.Bounds} Enabled={button.IsEffectivelyEnabled}" :
            $"TEXT {((TextBlock)control).Text} {control.Bounds} Visible={control.IsEffectivelyVisible}"));

    private static RenderTargetBitmap RenderPanelForParity(Control panel)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(panel.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(panel.Bounds.Height))), new Vector(96, 96));
        bitmap.Render(panel);
        return bitmap;
    }
}
