using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core.Models;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class CompactModerationLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340)]
    [InlineData(360, 400)]
    [InlineData(1100, 760)]
    public async Task BanPanelFitsAndControlsAreReachable(int width, int height)
    {
        var window = new MainWindow { MinWidth = 280, MinHeight = 340, Width = width, Height = height,
            RequestedThemeVariant = ThemeVariant.Dark };
        var root = Assert.IsType<Grid>(window.Content);
        var overlay = window.FindControl<Border>("ModerationPanelOverlay")!;
        var card = window.FindControl<Border>("ModerationPanelCard")!;
        var banned = window.FindControl<Grid>("BannedUsersPanel")!;
        var ban = window.FindControl<Button>("BanByLoginButton")!;
        var close = card.GetLogicalDescendants().OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button) == "CloseModerationButton");
        var content = window.FindControl<Border>("ModerationContentCard")!;
        window.FindControl<StackPanel>("ModerationPlatformTabsPanel")!.IsVisible = false;
        window.FindControl<TextBlock>("ModerationPanelStatusLabel")!.IsVisible = false;
        var contentGrid = Assert.IsType<Grid>(content.Child);
        contentGrid.Children[1].IsVisible = false;
        var twitchContent = Assert.IsType<Grid>(contentGrid.Children[0]);
        twitchContent.Children[0].IsVisible = false;
        twitchContent.Children[2].IsVisible = false;
        banned.IsVisible = true;
        ban.Content = "Бан";
        banned.GetLogicalDescendants().OfType<Button>().Last().Content = "Разбан";
        var header = window.FindControl<Grid>("ModerationHeader")!;
        var headerTexts = header.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        headerTexts[0].Text = "Модерация";
        headerTexts[1].Text = "Канал: @wither_101";
        var selector = window.FindControl<ComboBox>("CompactModerationSectionSelector")!;
        selector.IsVisible = width < 860;
        window.FindControl<ScrollViewer>("ModerationTabsScrollViewer")!.IsVisible = width >= 860;
        string[] sections = ["AutoMod", "Баны и тайм-ауты", "Запросы на разбан"];
        for (var index = 0; index < sections.Length; index++)
        {
            ((ComboBoxItem)selector.Items[index]!).Content = sections[index];
            window.FindControl<StackPanel>("ModerationTabsPanel")!.Children.OfType<Button>().ElementAt(index).Content = sections[index];
        }
        selector.SelectedIndex = 1;
        ((Grid)selector.Parent!).Children.OfType<Button>().Single().Content = "Обновить";
        var login = banned.GetLogicalDescendants().OfType<TextBox>().Single();
        login.PlaceholderText = "Ник пользователя";
        var list = banned.GetLogicalDescendants().OfType<ListBox>().Single();
        var items = Enumerable.Range(1, 30).Select(index => new BannedUserViewModel(new BannedUser(
            $"user-{index}", "long_viewer_name_" + index, "LongViewerName" + index,
            DateTimeOffset.UtcNow, null, "Причина: повторяющиеся сообщения и ссылки."))).ToArray();
        list.ItemsSource = items;
        var closed = false;
        close.Command = new RelayCommand(() => { closed = true; overlay.IsVisible = false; });
        root.Children.Clear();
        root.Children.Add(overlay);
        overlay.IsVisible = true;

        try
        {
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var settled = window.CaptureRenderedFrame();
            foreach (var rowButton in list.GetVisualDescendants().OfType<Button>())
            {
                rowButton.Content = "Разбан";
            }
            using var frame = window.CaptureRenderedFrame();
            AssertInsideViewport(card, window, width, height);
            AssertInsideViewport(ban, window, width, height);
            AssertInsideViewport(login, window, width, height);
            AssertInsideViewport(close, window, width, height);
            Assert.True(list.Bounds.Height > 40, $"Ban list has no useful height: {list.Bounds}");
            SaveFrame(frame, $"moderation-panel-{width}x{height}.png");

            list.ScrollIntoView(items[^1]);
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var scrolled = window.CaptureRenderedFrame();
            Assert.NotNull(list.ContainerFromItem(items[^1]));

            var point = Center(close, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.True(closed, "Close button must receive a pointer click.");
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(280, 340)]
    [InlineData(360, 400)]
    [InlineData(1100, 760)]
    public async Task BanConfirmationFitsAndCanBeClosed(int width, int height)
    {
        var window = new MainWindow { MinWidth = 280, MinHeight = 340, Width = width, Height = height,
            RequestedThemeVariant = ThemeVariant.Dark };
        var root = Assert.IsType<Grid>(window.Content);
        var overlay = window.FindControl<Border>("ModerationDialogOverlay")!;
        var card = window.FindControl<Border>("ModerationDialogCard")!;
        var close = window.FindControl<Button>("CloseModerationDialogButton")!;
        var confirm = window.FindControl<Button>("ConfirmModerationButton")!;
        ((TextBlock)confirm.Content!).Text = "Выдать тайм-аут";
        var body = card.GetLogicalDescendants().OfType<ScrollViewer>().Single();
        var bodyPanel = Assert.IsType<StackPanel>(body.Content);
        ((TextBlock)bodyPanel.Children[0]).Text = "Длительность тайм-аута";
        var durationPanel = Assert.IsType<WrapPanel>(bodyPanel.Children[1]);
        string[] durations = ["Навсегда", "1 мин", "10 мин", "1 час", "1 день"];
        for (var index = 0; index < durations.Length; index++)
        {
            durationPanel.Children.OfType<Button>().ElementAt(index).Content = durations[index];
        }
        durationPanel.Children.OfType<TextBlock>().Single().Text = "10 минут";
        ((TextBox)bodyPanel.Children[2]).PlaceholderText = "Причина (необязательно)";
        var header = card.GetLogicalDescendants().OfType<StackPanel>().First();
        header.Children.OfType<TextBlock>().First().Text = "Действие модерации";
        header.Children.OfType<TextBlock>().Last().Text = "@long_viewer_name";
        var footer = Assert.IsType<Grid>(confirm.Parent);
        footer.Children.OfType<Button>().First().Content = "Отмена";
        var closed = false;
        close.Command = new RelayCommand(() => { closed = true; overlay.IsVisible = false; });
        root.Children.Clear();
        root.Children.Add(overlay);
        overlay.IsVisible = true;

        try
        {
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var frame = window.CaptureRenderedFrame();
            AssertInsideViewport(card, window, width, height);
            AssertInsideViewport(close, window, width, height);
            AssertInsideViewport(confirm, window, width, height);
            SaveFrame(frame, $"moderation-dialog-{width}x{height}.png");

            var point = Center(close, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.True(closed, "Dialog close button must receive a pointer click.");
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void AssertInsideViewport(Control control, Window window, int width, int height)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0,
            $"Control has no size: {control.GetType().Name} {control.Bounds}");
        Assert.True(origin.X >= 0 && origin.Y >= 42 &&
                    origin.X + control.Bounds.Width <= width && origin.Y + control.Bounds.Height <= height,
            $"Control is clipped: {control.GetType().Name} at {origin}, bounds={control.Bounds}, window={width}x{height}");
    }

    private static void SaveFrame(Bitmap? frame, string name)
    {
        var output = Environment.GetEnvironmentVariable("WITHERCHAT_RENDER_OUTPUT");
        if (frame is null || string.IsNullOrWhiteSpace(output))
        {
            return;
        }
        Directory.CreateDirectory(output);
        frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
    }
}
