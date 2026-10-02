using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Core;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.ViewModels;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task ModerationLabelsAndPermissionsTrackRestoredSessionAndChannel()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.Channel = "first_channel";
        vm.ModerationUserLogin = "viewer";
        vm.CanModerate = true;
        Assert.False(vm.CanUnbanByLogin);
        Assert.False(vm.CanBanByLogin);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        ApplyAuditSession(vm);
        vm.CanModerate = true;
        Assert.Equal(vm.Texts.CurrentChannel + ": @" + vm.Channel, vm.ActiveChannelLabel);
        Assert.Contains(nameof(MainWindowViewModel.ActiveChannelLabel), notifications);
        Assert.True(vm.CanUnbanByLogin);
        Assert.True(vm.UnbanByLoginCommand.CanExecute(null));
        notifications.Clear();
        vm.Channel = "second_channel";
        Assert.Equal(vm.Texts.CurrentChannel + ": @second_channel", vm.ModerationChannelLabel);
        Assert.Contains(nameof(MainWindowViewModel.ModerationChannelLabel), notifications);
        vm.CanModerate = false;
        Assert.False(vm.CanUnbanByLogin);
        Assert.False(vm.UnbanByLoginCommand.CanExecute(null));
        vm.CanModerate = true;
        vm.Channel = string.Empty;
        Assert.Equal(vm.Texts.ModerationChannelRequired, vm.ModerationChannelLabel);
        Assert.False(vm.CanUnbanUsers);
        Assert.False(vm.CanBanByLogin);
    }

    [AvaloniaFact]
    public async Task ModerationEmptyStatesFollowCollectionsFiltersLoadingAndErrors()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Assert.True(vm.ShowNoAutoModMessages);
        Assert.True(vm.ShowNoBannedUsers);
        Assert.True(vm.ShowNoUnbanRequests);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var held = AuditHeldMessage();
        vm.PendingAutoModMessages.Add(held);
        Assert.False(vm.ShowNoAutoModMessages);
        Assert.Contains(nameof(MainWindowViewModel.ShowNoAutoModMessages), notifications);
        vm.PendingAutoModMessages.Clear();
        Assert.True(vm.ShowNoAutoModMessages);
        vm.UnbanRequests.Add(AuditUnbanRequest(UnbanRequestStatus.Approved));
        Assert.True(vm.ShowNoUnbanRequests);
        vm.UnbanRequestFilter = 1;
        Assert.False(vm.ShowNoUnbanRequests);
        vm.UnbanRequestFilter = 2;
        Assert.True(vm.ShowNoUnbanRequests);
        Assert.Equal(vm.Texts.NoDeniedUnbanRequests, vm.EmptyUnbanRequestsLabel);
        vm.IsModerationPanelBusy = true;
        Assert.False(vm.ShowNoAutoModMessages);
        Assert.False(vm.ShowNoBannedUsers);
        Assert.False(vm.ShowNoUnbanRequests);
        vm.IsModerationPanelBusy = false;
        vm.ModerationPanelStatus = "Offline test error";
        Assert.False(vm.ShowNoUnbanRequests);
        Assert.False(vm.ShowNoAutoModMessages);
        vm.ModerationPanelStatus = string.Empty;
        Assert.True(vm.ShowNoUnbanRequests);
    }

    [AvaloniaFact]
    public void NativeWindowsOptInToRoundedCorners()
    {
        var main = new MainWindow();
        var donations = new DonationAlertsWindow();
        Assert.Equal(Win32Properties.WindowCornerPreference.Round, Win32Properties.GetWindowCornerPreference(main));
        Assert.Equal(Win32Properties.WindowCornerPreference.Round, Win32Properties.GetWindowCornerPreference(donations));
        main.Content = null;
        main.Close();
        donations.Content = null;
        donations.Close();
    }

    [AvaloniaTheory]
    [InlineData(280, 340, false, 0)]
    [InlineData(280, 340, false, 2)]
    [InlineData(360, 400, false, 0)]
    [InlineData(360, 400, false, 2)]
    [InlineData(1100, 760, false, 0)]
    [InlineData(1100, 760, false, 2)]
    [InlineData(280, 340, true, 0)]
    [InlineData(280, 340, true, 2)]
    [InlineData(360, 400, true, 0)]
    [InlineData(360, 400, true, 2)]
    [InlineData(1100, 760, true, 0)]
    [InlineData(1100, 760, true, 2)]
    public async Task ModerationAuditRowsFitAndActionsStayReachable(int width, int height, bool light, int section)
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        window.MinWidth = 280;
        window.MinHeight = 340;
        window.Width = width;
        window.Height = height;
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        vm.Channel = "long_channel_name";
        ApplyAuditSession(vm);
        vm.CanModerate = true;
        vm.IsCompactMode = width < 860;
        vm.ModerationPanelSection = section;
        vm.PendingAutoModMessages.Add(AuditHeldMessage());
        vm.UnbanRequests.Add(AuditUnbanRequest(UnbanRequestStatus.Pending));
        var root = Assert.IsType<Grid>(window.Content);
        var overlay = window.FindControl<Border>("ModerationPanelOverlay")!;
        root.Children.Clear();
        root.Children.Add(overlay);
        vm.IsModerationPanelOpen = true;
        overlay.IsVisible = true;
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        using var initial = window.CaptureRenderedFrame();
        var list = window.FindControl<ListBox>(section == 0 ? "AutoModMessagesList" : "UnbanRequestsList")!;
        Assert.True(list.Bounds.Width > 160 && list.Bounds.Height > 40, $"Unusable list: {list.Bounds}");
        var item = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(0));
        var buttons = item.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible).ToArray();
        Assert.Equal(2, buttons.Length);
        foreach (var button in buttons)
        {
            var clicked = false;
            button.Command = new RelayCommand(() => clicked = true); // Never call a real moderation API.
            button.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            using var settled = window.CaptureRenderedFrame();
            AssertAuditControlInside(button, window, width, height);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.True(clicked, "Visible action must receive a pointer click.");
        }
        if (section == 2)
        {
            var filters = window.FindControl<WrapPanel>("UnbanRequestFilters")!;
            foreach (var button in filters.Children.OfType<Button>())
            {
                AssertAuditControlInside(button, window, width, height);
            }
        }
        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame, $"moderation-{section}-{width}x{height}-{(light ? "light" : "dark")}.png");
        var close = overlay.GetLogicalDescendants().OfType<Button>().Single(button =>
            AutomationProperties.GetAutomationId(button) == "CloseModerationButton");
        AssertAuditControlInside(close, window, width, height);
        var closePoint = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseDown(closePoint, MouseButton.Left);
        window.MouseUp(closePoint, MouseButton.Left);
        Assert.False(vm.IsModerationPanelOpen);
    }

    private static void ApplyAuditSession(MainWindowViewModel vm) =>
        typeof(MainWindowViewModel).GetMethod("ApplyAuthenticatedSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [new TwitchAuthSession("test-token", string.Empty, TwitchApplication.ClientId,
                "owner-id", "audit_owner", TwitchApplication.RequiredScopes,
                DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow)]);

    private static HeldAutoModMessageViewModel AuditHeldMessage() => new(new HeldAutoModMessage(
        "held-id", "channel-id", "long_channel_name", "viewer-id", "long_viewer_login_name",
        "ОченьДлинноеИмяЗрителяДляПроверки",
        "Длинное удержанное сообщение: " + string.Concat(Enumerable.Repeat("текст сообщения ", 24)),
        "Очень длинная категория модерации", 3, DateTimeOffset.UtcNow))
        { ErrorMessage = "Ошибка проверки: длинное пояснение должно переноситься, не сжимая кнопки." };

    private static UnbanRequestViewModel AuditUnbanRequest(UnbanRequestStatus status) => new(new UnbanRequest(
        "request-id", "channel-id", "viewer-id", "long_viewer_login_name",
        "ОченьДлинноеИмяЗрителяДляПроверки",
        "Причина запроса: " + string.Concat(Enumerable.Repeat("подробное объяснение ", 20)),
        DateTimeOffset.UtcNow, status, null, string.Empty));

    private static void AssertAuditControlInside(Control control, Window window, int width, int height)
    {
        var origin = control.TranslatePoint(default, window)!.Value;
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0 &&
                    origin.X >= 0 && origin.Y >= 42 &&
                    origin.X + control.Bounds.Width <= width + 0.01 &&
                    origin.Y + control.Bounds.Height <= height + 0.01,
            $"Clipped {control.GetType().Name}: origin={origin}, bounds={control.Bounds}, window={width}x{height}");
    }

    private static void SaveAuditFrame(Bitmap? frame, string name)
    {
        var output = Environment.GetEnvironmentVariable("WITHERCHAT_RENDER_OUTPUT");
        if (frame is null || string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        frame.Save(Path.Combine(output, name), PngBitmapEncoderOptions.Default);
    }
}
