using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, true)]
    [InlineData(360, 400, true)]
    [InlineData(1100, 760, true)]
    [InlineData(360, 400, false)]
    public async Task SystemThemeAuditSystemThemeNicknameFollowsEffectiveWindowTheme(int width, int height, bool light)
    {
        var app = Application.Current!;
        var oldTheme = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            await using var fixture = new WindowFixture();
            ConfigureAuxiliaryAuditWindow(fixture, width, height, "ru", light);
            fixture.Window.RequestedThemeVariant = ThemeVariant.Default;
            var vm = fixture.ViewModel;
            vm.Theme = "System";
            vm.Channel = "audit";
            var item = new ChatMessageItemViewModel(NicknameMessage(light ? "#FFFFFF" : "#000000"),
                fixture.ImageCache, vm.Texts, owner: vm);
            vm.Messages.Add(item);
            typeof(MainWindowViewModel).GetMethod("RebuildVisibleMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
            fixture.Window.Show();
            await Task.Delay(450);
            await SettleAuxiliaryAuditAsync(fixture.Window);
            Assert.Single(vm.VisibleMessages);
            Assert.Equal(light ? ThemeVariant.Light : ThemeVariant.Dark, fixture.Window.ActualThemeVariant);
            var rendered = fixture.Window.GetVisualDescendants().OfType<RichChatTextBlock>()
                .Where(control => control.IsEffectivelyVisible && ReferenceEquals(control.DataContext, item)).ToArray();
            Assert.NotEmpty(rendered);
            Assert.All(rendered, control => Assert.True(control.Bounds.Width > 0 && control.TextLayout.Width > 0));
            using var frame = fixture.Window.CaptureRenderedFrame();
            SaveAuditFrame(frame, $"system-{(light ? "light" : "dark")}-{width}x{height}.png");
            Assert.True(SystemThemeAuditContrast(BrushColor(item.UserBrush), Color.Parse(light ? "#D7DDF0" : "#252833")) >= 4.5,
                $"System theme nickname is unreadable: {BrushColor(item.UserBrush)}, effective={fixture.Window.ActualThemeVariant}");
        }
        finally { app.RequestedThemeVariant = oldTheme; }
    }

    [AvaloniaFact]
    public async Task SystemThemeAuditChangingEffectiveSystemThemeRefreshesExistingNickname()
    {
        var app = Application.Current!;
        var oldTheme = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            await using var fixture = new WindowFixture();
            fixture.Window.RequestedThemeVariant = ThemeVariant.Default;
            var vm = fixture.ViewModel;
            vm.Theme = "System";
            vm.Channel = "audit";
            var item = new ChatMessageItemViewModel(NicknameMessage("#FFFFFF"),
                fixture.ImageCache, vm.Texts, owner: vm);
            vm.Messages.Add(item);
            typeof(MainWindowViewModel).GetMethod("RebuildVisibleMessages", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
            fixture.Window.Show();
            await Task.Delay(450);
            await SettleAuxiliaryAuditAsync(fixture.Window);
            Assert.Single(vm.VisibleMessages);
            app.RequestedThemeVariant = ThemeVariant.Light;
            await SettleAuxiliaryAuditAsync(fixture.Window);
            Assert.Equal(ThemeVariant.Light, fixture.Window.ActualThemeVariant);
            Assert.True(SystemThemeAuditContrast(BrushColor(item.UserBrush), Color.Parse("#D7DDF0")) >= 4.5,
                "Existing nickname still uses dark-theme colors after the System theme becomes light");
        }
        finally { app.RequestedThemeVariant = oldTheme; }
    }

    [AvaloniaTheory]
    [InlineData("""{"displayName":"Viewer","text":"message","badges":[null]}""")]
    [InlineData("""{"displayName":"Viewer","text":"message","parts":[null]}""")]
    public void SystemThemeAuditMalformedLogItemsDoNotBreakLogOpening(string line)
    {
        using var cache = new WitherChat.Desktop.Services.ChatImageCache();
        var entry = ChatLogEntryViewModel.Parse(line);
        entry.ApplyPresentation(cache, null, null, true, true, true);
        Assert.Equal("message", entry.Text);
    }

    private static double SystemThemeAuditContrast(Color first, Color second)
    {
        static double Linear(byte value)
        {
            var c = value / 255d;
            return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        }
        static double Lum(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var a = Lum(first); var b = Lum(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    [AvaloniaTheory]
    [InlineData("Light", false, true)]
    [InlineData("Dark", true, false)]
    [InlineData("System", false, false)]
    [InlineData("System", true, true)]
    public async Task EffectiveMessageThemePreservesExplicitPreference(string preference, bool actualLight, bool expectedLight)
    {
        await using var fixture = new WindowFixture();
        fixture.ViewModel.Theme = preference;
        fixture.ViewModel.UpdateActualTheme(actualLight);
        Assert.Equal(expectedLight, fixture.ViewModel.UseLightMessageTheme);
        Assert.Equal(preference, fixture.ViewModel.Theme);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EffectiveSystemThemeUpdatesStoredDeferredRecentAndLogItems(bool light)
    {
        var app = Application.Current!;
        var oldTheme = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = light ? ThemeVariant.Dark : ThemeVariant.Light;
            await using var fixture = new WindowFixture();
            fixture.Window.RequestedThemeVariant = ThemeVariant.Default;
            var vm = fixture.ViewModel;
            vm.Theme = "System";
            vm.Channel = "audit";
            var sourceColor = light ? "#FFFFFF" : "#000000";
            var live = new ChatMessageItemViewModel(NicknameMessage(sourceColor), fixture.ImageCache, vm.Texts, owner: vm);
            var deferred = new ChatMessageItemViewModel(NicknameMessage(sourceColor), fixture.ImageCache, vm.Texts, owner: vm);
            var recent = new ChatMessageItemViewModel(NicknameMessage(sourceColor), fixture.ImageCache, vm.Texts, owner: vm);
            vm.Messages.Add(live);
            ((List<ChatMessageItemViewModel>)typeof(MainWindowViewModel).GetField("_deferredMessages",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!).Add(deferred);
            vm.RecentUserMessages.Add(new RecentUserMessageViewModel(recent));

            Directory.CreateDirectory(vm.ChatLogDirectory);
            var path = Path.Combine(vm.ChatLogDirectory, "theme.jsonl");
            await File.WriteAllTextAsync(path,
                System.Text.Json.JsonSerializer.Serialize(new { displayName = "Viewer", color = sourceColor, text = "message" }) + "\n");
            vm.SelectedLogFile = new ChatLogFileViewModel(path, "audit", DateTime.Today, new FileInfo(path).Length);
            await WaitForAsync(() => vm.ChatLogEntries.Count == 1 && vm.ChatLogEntries[0].HasPresentation, TimeSpan.FromSeconds(3));
            fixture.Window.Show();
            await SettleAuxiliaryAuditAsync(fixture.Window);
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            await SettleAuxiliaryAuditAsync(fixture.Window);
            var expected = BrushColor(WitherChat.Desktop.Services.ChatUserColor.Create(sourceColor, light));
            foreach (var item in new[] { live, deferred, recent })
            {
                Assert.Equal(expected, BrushColor(item.UserBrush));
                Assert.Equal(sourceColor, item.Message.UserColor);
            }
            Assert.Equal(expected, BrushColor(vm.ChatLogEntries[0].UserBrush!));
            Assert.Equal("System", vm.Theme);
        }
        finally { app.RequestedThemeVariant = oldTheme; }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EffectiveSystemThemeSelectsMatchingTwitchEmoteVariant(bool light)
    {
        await using var fixture = new WindowFixture();
        using var cache = new WitherChat.Desktop.Services.ChatImageCache(new MissingImageHandler());
        var vm = fixture.ViewModel;
        vm.Theme = "System";
        vm.UpdateActualTheme(!light);
        var emote = WitherChat.Core.Models.ChatMessagePart.TwitchEmote("Kappa", "25");
        var message = NicknameMessage("#FFFFFF") with { Parts = [emote] };
        var item = new ChatMessageItemViewModel(message, cache, vm.Texts, owner: vm);
        vm.Messages.Add(item);
        _ = item.Parts;
        vm.UpdateActualTheme(light);
        Assert.Same(cache.GetResource(emote.GetImageUri(light)!, 28), Assert.Single(item.Parts).ImageResource);
    }

    [AvaloniaTheory]
    [InlineData("""{"displayName":"Viewer","text":"message","badges":[{"setId":null,"versionId":"1"}]}""")]
    [InlineData("""{"displayName":"Viewer","text":"message","parts":[{"kind":"Text","text":null}]}""")]
    [InlineData("""{"displayName":"Viewer","text":"message","parts":[{"kind":555,"text":"invalid"}]}""")]
    [InlineData("""{"displayName":"Viewer","text":"message","badges":null,"parts":null}""")]
    [InlineData("""{"displayName":"Viewer","text":"message","badges":[null,{"setId":"subscriber","versionId":"1"}],"parts":[null,{"kind":"Text","text":"message"}]}""")]
    public void DamagedLogMetadataPreservesTextAndValidElements(string line)
    {
        using var cache = new WitherChat.Desktop.Services.ChatImageCache(new MissingImageHandler());
        var entry = ChatLogEntryViewModel.Parse(line);
        entry.ApplyPresentation(cache, null, null, true, false, false);
        Assert.Equal("message", entry.Text);
        Assert.Equal(line, entry.RawText);
        Assert.Equal("message", Assert.Single(entry.Parts).Text);
        if (line.Contains("subscriber", StringComparison.Ordinal))
        {
            Assert.Equal("subscriber", entry.Role);
            Assert.Single(entry.BadgeData);
            Assert.Single(entry.Badges);
        }
    }

    [AvaloniaFact]
    public async Task MalformedLogCanBeOpenedAndConvertedForExport()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Directory.CreateDirectory(vm.ChatLogDirectory);
        var source = Path.Combine(vm.ChatLogDirectory, "damaged.log");
        const string line = """{"displayName":"Viewer","text":"message","badges":[null],"parts":[null]}""";
        await File.WriteAllTextAsync(source, line + "\n");
        vm.SelectedLogFile = new ChatLogFileViewModel(source, "audit", DateTime.Today, new FileInfo(source).Length);
        await WaitForAsync(() => vm.ChatLogEntries.Count == 1 && vm.ChatLogEntries[0].HasPresentation, TimeSpan.FromSeconds(3));
        Assert.Equal("message", Assert.Single(vm.ChatLogEntries).Text);
        var output = new MemoryStream();
        await WitherChat.Desktop.Views.MainWindow.ExportLogToStreamAsync(source, null,
            () => Task.FromResult<Stream>(output), jsonLines: true);
        using var document = System.Text.Json.JsonDocument.Parse(output.ToArray());
        Assert.Equal("Viewer", document.RootElement.GetProperty("user").GetString());
        Assert.Equal("message", document.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative.png")]
    [InlineData("https://static-cdn.jtvnw.net/emoticons/v2/25/animated/dark/2.0")]
    public void LightTwitchEmoteVariantHandlesAbsentOrRelativeSourceUri(string? source)
    {
        var part = WitherChat.Core.Models.ChatMessagePart.TwitchEmote("Kappa", "25") with
        {
            ImageUri = source is null ? null : new Uri(source, UriKind.RelativeOrAbsolute)
        };
        var uri = part.GetImageUri(true);
        Assert.NotNull(uri);
        Assert.True(uri.IsAbsoluteUri);
        Assert.Equal("static-cdn.jtvnw.net", uri.Host);
        Assert.EndsWith("/light/2.0", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(source?.Contains("/animated/", StringComparison.Ordinal) == true,
            uri.AbsolutePath.Contains("/animated/", StringComparison.Ordinal));
        Assert.Same(part.ImageUri, part.GetImageUri(false));
    }

}
