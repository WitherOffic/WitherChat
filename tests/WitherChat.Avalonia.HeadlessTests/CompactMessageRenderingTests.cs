using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WitherChat.Core.Models;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;
using System.Net;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class CompactMessageRenderingTests
{
    [AvaloniaTheory]
    [InlineData(2, true, false)]
    [InlineData(5, true, false)]
    [InlineData(5, false, false)]
    [InlineData(5, true, true)]
    public async Task ExistingMessagesKeepRenderedTextAfterCompactResize(
        int messageCount, bool hideDuringResize, bool includeEmotes)
    {
        using var cache = new ChatImageCache(new TestImageHandler());
        var emote = ChatMessagePart.TwitchEmote("Kappa", "25", isAnimated: false);
        if (includeEmotes)
        {
            await cache.PreloadAsync([emote.ImageUri]);
        }
        var messages = Enumerable.Range(0, messageCount).Select(index => new ChatMessageItemViewModel(
            new ChatMessage
            {
                Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Channel = "audit",
                UserLogin = "viewer",
                DisplayName = "Viewer",
                Text = "Сообщение до переключения режима",
                Timestamp = DateTimeOffset.UtcNow,
                Parts = includeEmotes
                    ? [ChatMessagePart.PlainText("Сообщение до переключения "), emote]
                    : []
            }, cache, new UiText())).ToArray();
        var list = new ListBox
        {
            ItemsSource = messages,
            ItemTemplate = new FuncDataTemplate<ChatMessageItemViewModel>((item, _) =>
                new RichChatTextBlock
                {
                    Parts = item!.Parts,
                    PrefixText = item.UserLabelWithColon,
                    PrefixBrush = Brushes.Cyan,
                    Foreground = Brushes.White,
                    FontSize = 17,
                    TextWrapping = TextWrapping.Wrap
                })
        };
        var window = new Window
        {
            Width = 1100, Height = 760, Content = list, Background = Brushes.Black
        };
        try
        {
            window.Show();
            using var initial = window.CaptureRenderedFrame();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var settled = window.CaptureRenderedFrame();
            var textBlocks = list.GetVisualDescendants().OfType<RichChatTextBlock>().ToArray();
            Assert.Equal(messages.Length, textBlocks.Length);
            Assert.All(textBlocks, block => Assert.True(block.TextLayout.Width > 100, "Initial message must render."));
            foreach (var compact in new[] { true, false, true })
            {
                list.IsVisible = !hideDuringResize;
                foreach (var textBlock in textBlocks)
                {
                    textBlock.UseCompactEmotes = compact;
                }
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
                window.Width = compact ? 360 : 1100;
                window.Height = compact ? 400 : 760;
                using var hidden = window.CaptureRenderedFrame();
                list.IsVisible = true;
                list.InvalidateMeasure();
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
                using var shown = window.CaptureRenderedFrame();
                using var finalFrame = window.CaptureRenderedFrame();
                Assert.All(list.GetVisualDescendants().OfType<RichChatTextBlock>(), block =>
                {
                    Assert.True(block.IsEffectivelyVisible);
                    Assert.True(block.Bounds.Width > 0);
                    Assert.True(block.TextLayout.Width > 100,
                        $"Text disappeared after compact={compact}: inlines={block.Inlines?.Count}, bounds={block.Bounds}, textWidth={block.TextLayout.Width}");
                    if (includeEmotes)
                    {
                        var panel = Assert.IsType<Grid>(Assert.Single(
                            block.Inlines!.OfType<InlineUIContainer>()).Child);
                        Assert.Equal(compact ? 22 : 28, panel.Bounds.Height);
                        Assert.Contains(panel, block.GetVisualDescendants());
                    }
                });
                var captureDirectory = Environment.GetEnvironmentVariable("WITHERCHAT_RENDER_OUTPUT");
                if (compact && !string.IsNullOrEmpty(captureDirectory))
                {
                    Directory.CreateDirectory(captureDirectory);
                    finalFrame!.Save(Path.Combine(captureDirectory,
                        $"compact-{messageCount}-hidden-{hideDuringResize}-emotes-{includeEmotes}.png"),
                        PngBitmapEncoderOptions.Default);
                }
            }
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private sealed class TestImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Convert.FromBase64String(
                    "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
                    "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7"))
            });
    }
}
