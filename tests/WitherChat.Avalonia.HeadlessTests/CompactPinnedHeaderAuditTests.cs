using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, false)]
    [InlineData(280, 340, true)]
    [InlineData(360, 400, false)]
    [InlineData(360, 400, true)]
    [InlineData(1100, 760, false)]
    [InlineData(1100, 760, true)]
    public async Task PinnedHeaderKeepsRussianLabelAndLongAuthorInsideCompactViewport(
        int width, int height, bool light)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.MinWidth = 280;
        window.MinHeight = 340;
        window.Width = width;
        window.Height = height;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        viewModel.ReduceMotion = true;
        viewModel.Language = "ru";
        viewModel.IsCompactMode = width < 860;
        window.Show();

        async Task SettleAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var initial = window.CaptureRenderedFrame();
            using var settled = window.CaptureRenderedFrame();
        }

        var card = window.FindControl<Border>("PinnedMessageCard")!;
        var header = window.FindControl<WrapPanel>("PinnedHeaderPanel")!;
        var label = window.FindControl<TextBlock>("PinnedMessageLabel")!;
        var author = window.FindControl<TextBlock>("PinnedMessageAuthorLabel")!;

        void VerifyHeader()
        {
            Assert.True(card.IsEffectivelyVisible);
            foreach (var text in new[] { label, author })
            {
                var origin = text.TranslatePoint(default, header)!.Value;
                Assert.True(origin.X >= 0 && origin.Y >= 0);
                Assert.True(origin.X + text.Bounds.Width <= header.Bounds.Width + 0.5 &&
                            origin.Y + text.Bounds.Height <= header.Bounds.Height + 0.5,
                    $"Pinned header text is clipped: origin={origin}, text={text.Bounds}, header={header.Bounds}");
                var windowOrigin = text.TranslatePoint(default, window)!.Value;
                Assert.True(windowOrigin.X >= 0 && windowOrigin.Y >= 42);
                Assert.True(windowOrigin.X + text.Bounds.Width <= width + 0.5 &&
                            windowOrigin.Y + text.Bounds.Height <= height + 0.5);
                Assert.True(text.TextLayout.Width > 0 && text.TextLayout.Width <= header.Bounds.Width + 0.5);
            }
        }

        // The initial welcome preview already reproduces the Russian label +
        // WitherChat author overflow at 280 pixels, without a real account.
        await SettleAsync();
        Assert.Equal("WitherChat", author.Text);
        VerifyHeader();

        viewModel.PinnedMessageAuthor = "VeryLongPinnedAuthorWithManyCharacters_Wither_101";
        viewModel.PinnedMessageText = "A pinned message for a narrow-window regression.";
        await SettleAsync();
        VerifyHeader();
        Assert.Equal(TextTrimming.CharacterEllipsis, author.TextTrimming);
        Assert.Equal(viewModel.PinnedMessageAuthor, ToolTip.GetTip(author));

        var labelOrigin = label.TranslatePoint(default, header)!.Value;
        var authorOrigin = author.TranslatePoint(default, header)!.Value;
        if (width < 860)
        {
            Assert.True(authorOrigin.Y >= labelOrigin.Y + label.Bounds.Height - 0.5,
                "The author should use a second header row instead of disappearing beyond the right edge.");
        }
        else
        {
            Assert.Equal(labelOrigin.Y, authorOrigin.Y, precision: 1);
            Assert.Equal(labelOrigin.X + label.Bounds.Width + 8, authorOrigin.X, precision: 1);
        }

        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"pinned-header-ru-{(light ? "light" : "dark")}-{width}x{height}.png");
    }
}
