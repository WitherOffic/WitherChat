using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core.Models;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class RecentMessagesLayoutTests
{
    [AvaloniaTheory]
    [InlineData(360, 400)]
    [InlineData(280, 340)]
    [InlineData(1100, 760)]
    public async Task RecentMessagesFitViewportAndKeepCloseButtonReachable(int width, int height)
    {
        using var cache = new ChatImageCache();
        var window = new MainWindow { MinWidth = 280, MinHeight = 340, Width = width, Height = height };
        var root = Assert.IsType<Grid>(window.Content);
        var overlay = root.Children.OfType<Border>().Single(control =>
            AutomationProperties.GetAutomationId(control) == "RecentMessagesOverlay");
        // Use the production panel without authentication, storage or network
        // services: this regression concerns its layout and pointer hit testing.
        root.Children.Clear();
        root.Children.Add(overlay);
        overlay.IsVisible = true;
        var card = window.FindControl<Border>("RecentMessagesCard")!;
        var close = card.GetLogicalDescendants().OfType<Button>().Single();
        var header = card.GetLogicalDescendants().OfType<StackPanel>().First();
        header.Children.OfType<TextBlock>().First().Text = "Последние сообщения";
        header.Children.OfType<TextBlock>().Last().Text = "LongViewerName · @long_viewer_name";
        var list = card.GetLogicalDescendants().OfType<ListBox>().Single();
        var items = Enumerable.Range(1, 50).Select(index => new RecentUserMessageViewModel(
            new ChatMessageItemViewModel(new ChatMessage
            {
                Id = $"recent-{index}",
                Channel = "audit", UserLogin = "viewer", DisplayName = "Viewer",
                Text = $"Сообщение {index}: длинный текст должен переноситься и оставаться доступным.",
                Timestamp = DateTimeOffset.UtcNow
            }, cache, new UiText()))).ToArray();
        list.ItemsSource = items;
        var emptyLabel = card.GetLogicalDescendants().OfType<TextBlock>()
            .Single(control => control.HorizontalAlignment == HorizontalAlignment.Center &&
                               control.VerticalAlignment == VerticalAlignment.Center);
        emptyLabel.IsVisible = false;
        var clicked = false;
        close.Command = new RelayCommand(() =>
        {
            clicked = true;
            overlay.IsVisible = false;
        });

        try
        {
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var settled = window.CaptureRenderedFrame();
            using var frame = window.CaptureRenderedFrame();
            var origin = card.TranslatePoint(default, window)!.Value;
            Assert.True(origin.X >= 0 && origin.Y >= 42, $"Card starts outside content area: {origin}");
            Assert.True(origin.X + card.Bounds.Width <= width && origin.Y + card.Bounds.Height <= height,
                $"Card is clipped: origin={origin}, bounds={card.Bounds}, window={width}x{height}");
            Assert.True(list.Bounds.Width > width * 0.5 || list.Bounds.Width > 400);
            Assert.True(list.Bounds.Height > 80);

            list.ScrollIntoView(items[^1]);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var scrolled = window.CaptureRenderedFrame();
            var lastRow = list.ContainerFromItem(items[^1]);
            Assert.NotNull(lastRow);
            Assert.True(lastRow!.IsEffectivelyVisible);

            var output = Environment.GetEnvironmentVariable("WITHERCHAT_RENDER_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                scrolled!.Save(Path.Combine(output, $"recent-messages-{width}x{height}.png"),
                    PngBitmapEncoderOptions.Default);
            }

            var closePoint = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            Assert.True(closePoint.X > 0 && closePoint.X < width && closePoint.Y > 42 && closePoint.Y < height);
            Assert.True(close.IsEffectivelyEnabled, "The test close command must be enabled.");
            window.MouseMove(closePoint);
            window.MouseDown(closePoint, MouseButton.Left);
            window.MouseUp(closePoint, MouseButton.Left);
            Assert.True(clicked, "The visible close button must receive a real pointer click.");
            Assert.False(overlay.IsVisible);
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }
}
