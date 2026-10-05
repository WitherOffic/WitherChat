using System.Globalization;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task LifecycleEventAuditQueuedStatusCannotReconnectAfterDisconnectOrDispose(bool youtube, bool dispose)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        if (youtube)
        {
            Round9ApplyYouTube(vm);
            vm.YouTubeStatus = "closing";
            Round9Invoke(vm, "OnYouTubeStatusChanged",
                new ChatConnectionStatusEventArgs(ChatConnectionState.Connected, "youtube_UC-owner"));
        }
        else
        {
            ApplyDonationAlertsSessionForTesting(vm);
            vm.DonationAlertsStatus = "closing";
            Round9Invoke(vm, "OnDonationAlertsStatusChanged",
                new DonationAlertsConnectionStatusEventArgs(true, false, "connected"));
        }

        if (dispose) await vm.DisposeAsync();
        else if (youtube) await vm.DisconnectYouTubeCommand.ExecuteAsync(null);
        else await vm.DisconnectDonationAlertsCommand.ExecuteAsync(null);
        await Round9PumpAsync();
        if (youtube)
        {
            Assert.False(vm.IsYouTubeLiveConnected);
            Assert.False(vm.IsYouTubeConnecting);
            Assert.Equal(dispose ? "closing" : vm.Texts.YouTubeNotConnected, vm.YouTubeStatus);
        }
        else
        {
            Assert.False(vm.IsDonationAlertsRealtimeConnected);
            Assert.False(vm.IsDonationAlertsReconnecting);
            Assert.Equal(dispose ? "closing" : vm.Texts.DonationAlertsNotConnected, vm.DonationAlertsStatus);
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LifecycleEventAuditQueuedStatusFromOldAccountCannotChangeNewAccount(bool youtube)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        if (youtube)
        {
            Round9ApplyYouTube(vm);
            Round9Invoke(vm, "OnYouTubeStatusChanged",
                new ChatConnectionStatusEventArgs(ChatConnectionState.Connected, "youtube_UC-owner"));
            Round9ApplyYouTube(vm, "UC-new");
            vm.YouTubeStatus = "new account connecting";
        }
        else
        {
            ApplyDonationAlertsSessionForTesting(vm);
            Round9Invoke(vm, "OnDonationAlertsStatusChanged",
                new DonationAlertsConnectionStatusEventArgs(true, false, "connected"));
            Round9InvokeSession(vm, "ApplyDonationAlertsSession", new DonationAlertsAuthSession(
                "test-new-access", "client", 43, "new-owner", "New owner", null, [],
                DateTimeOffset.UtcNow.AddHours(1)));
            vm.DonationAlertsStatus = "new account connecting";
        }
        await Round9PumpAsync();
        Assert.False(youtube ? vm.IsYouTubeLiveConnected : vm.IsDonationAlertsRealtimeConnected);
        Assert.Equal("new account connecting", youtube ? vm.YouTubeStatus : vm.DonationAlertsStatus);
    }

    [AvaloniaTheory]
    [InlineData("disconnected")]
    [InlineData("disposed")]
    [InlineData("different-account")]
    [InlineData("queued-before-replacement")]
    public async Task LifecycleEventAuditLateYouTubeRefreshCannotRestoreOrReplaceAccount(string mode)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var original = Round9ApplyYouTube(vm);
        var refresh = original with { AccessToken = "test-refreshed-access" };
        var store = Round9Field<YouTubeAuthSessionStore>(vm, "_youTubeAuthSessionStore");
        if (mode == "queued-before-replacement")
            Round9Invoke(vm, "OnYouTubeSessionUpdated", new YouTubeSessionEventArgs(refresh));
        if (mode == "disconnected") await vm.DisconnectYouTubeCommand.ExecuteAsync(null);
        else if (mode == "disposed") await vm.DisposeAsync();
        else
        {
            var replacement = Round9ApplyYouTube(vm, "UC-new");
            store.Save(replacement);
        }
        if (mode != "queued-before-replacement")
            Round9Invoke(vm, "OnYouTubeSessionUpdated", new YouTubeSessionEventArgs(refresh));
        await Round9PumpAsync();
        var current = Round9Field<YouTubeAuthSession?>(vm, "_youTubeAuthSession");
        if (mode == "disconnected") { Assert.Null(current); Assert.Null(store.Load()); }
        else if (mode == "disposed") { Assert.Equal(original, current); Assert.Null(store.Load()); }
        else
        {
            Assert.Equal("UC-new", current!.ChannelId);
            Assert.Equal("UC-new", store.Load()!.ChannelId);
        }
    }

    [AvaloniaFact]
    public async Task LifecycleEventAuditValidYouTubeRefreshIsStillSaved()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var session = Round9ApplyYouTube(vm) with { AccessToken = "test-refreshed-access" };
        Round9Invoke(vm, "OnYouTubeSessionUpdated", new YouTubeSessionEventArgs(session));
        await Round9PumpAsync();
        Assert.Equal(session, Round9Field<YouTubeAuthSession>(vm, "_youTubeAuthSession"));
        Assert.Equal(session.AccessToken, Round9Field<YouTubeAuthSessionStore>(vm, "_youTubeAuthSessionStore").Load()!.AccessToken);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleEventAuditQueuedDonationCannotOpenWindowAfterDisconnectOrDispose(bool dispose)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyDonationAlertsSessionForTesting(vm);
        vm.DonationAlertsAutoOpenWindow = true;
        var windowRequests = 0;
        vm.DonationWindowRequested += (_, _) => windowRequests++;
        Round9Invoke(vm, "OnDonationReceived", new DonationAlertEventArgs(Round9Donation(500m)));
        if (dispose) await vm.DisposeAsync();
        else await vm.DisconnectDonationAlertsCommand.ExecuteAsync(null);
        await Round9PumpAsync();
        Assert.Equal(0, windowRequests);
        Assert.Null(vm.CurrentDonation);
        Assert.Empty(vm.StreamEvents);
        Assert.Equal(0, vm.PendingMessageCount);
    }

    [AvaloniaFact]
    public async Task LifecycleEventAuditQueuedStreamEventCannotChangeUiAfterDispose()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.EnqueueStreamEventForTesting(new StreamEvent
        {
            Id = "audit-event", Platform = "Twitch", Channel = "audit",
            Kind = StreamEventKinds.Subscription, DisplayName = "Viewer", Timestamp = DateTimeOffset.UtcNow
        });
        await vm.DisposeAsync();
        await Round9PumpAsync();
        Assert.Empty(vm.StreamEvents);
        Assert.Empty(vm.VisibleStreamEvents);
    }

    [AvaloniaTheory]
    [InlineData("500.25", 500250000L)]
    [InlineData("0.0000005", 0L)]
    [InlineData("0.0000015", 2L)]
    [InlineData("9223372036854.775807", long.MaxValue)]
    [InlineData("-9223372036854.775808", long.MinValue)]
    [InlineData("9223372036855", long.MaxValue)]
    [InlineData("-9223372036855", long.MinValue)]
    [InlineData("79228162514264337593543950335", long.MaxValue)]
    [InlineData("-79228162514264337593543950335", long.MinValue)]
    public async Task LifecycleEventAuditDonationAmountCannotThrowOnUiThread(string text, long expectedMicros)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyDonationAlertsSessionForTesting(vm);
        vm.DonationAlertsAutoOpenWindow = false;
        var amount = decimal.Parse(text, CultureInfo.InvariantCulture);
        var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            errors.Add(args.Exception);
            args.Handled = true;
        };
        Dispatcher.UIThread.UnhandledException += handler;
        try
        {
            Round9Invoke(vm, "OnDonationReceived", new DonationAlertEventArgs(Round9Donation(amount)));
            await Round9PumpAsync();
            Assert.Empty(errors);
            var value = Assert.Single(vm.StreamEvents).Value;
            Assert.Equal(expectedMicros, value.AmountMicros);
            Assert.Equal(amount.ToString("0.##", CultureInfo.CurrentCulture) + " RUB", value.AmountDisplay);
            Assert.NotNull(vm.CurrentDonation);
        }
        finally { Dispatcher.UIThread.UnhandledException -= handler; }
    }

    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LifecycleEventAuditCurrentAccountStillReceivesLiveStatus(bool youtube, bool connected)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        if (youtube)
        {
            Round9ApplyYouTube(vm);
            Round9Invoke(vm, "OnYouTubeStatusChanged", new ChatConnectionStatusEventArgs(
                connected ? ChatConnectionState.Connected : ChatConnectionState.Reconnecting, "youtube_UC-owner"));
        }
        else
        {
            ApplyDonationAlertsSessionForTesting(vm);
            Round9Invoke(vm, "OnDonationAlertsStatusChanged",
                new DonationAlertsConnectionStatusEventArgs(connected, !connected, "audit"));
        }
        await Round9PumpAsync();
        Assert.Equal(connected, youtube ? vm.IsYouTubeLiveConnected : vm.IsDonationAlertsRealtimeConnected);
        Assert.Equal(!connected, youtube ? vm.IsYouTubeConnecting : vm.IsDonationAlertsReconnecting);
    }

    [AvaloniaFact]
    public async Task LifecycleEventAuditConsecutiveYouTubeRefreshesPreserveNewestSession()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var session = Round9ApplyYouTube(vm);
        Round9Invoke(vm, "OnYouTubeSessionUpdated",
            new YouTubeSessionEventArgs(session with { AccessToken = "test-refresh-first" }));
        var newest = session with { AccessToken = "test-refresh-second" };
        Round9Invoke(vm, "OnYouTubeSessionUpdated", new YouTubeSessionEventArgs(newest));
        await Round9PumpAsync();
        Assert.Equal(newest, Round9Field<YouTubeAuthSession>(vm, "_youTubeAuthSession"));
        Assert.Equal(newest.AccessToken, Round9Field<YouTubeAuthSessionStore>(vm, "_youTubeAuthSessionStore").Load()!.AccessToken);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleEventAuditQueuedYouTubeStreamEventCannotOutliveAccount(bool replacement)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Round9ApplyYouTube(vm);
        vm.EnqueueStreamEventForTesting(new StreamEvent
        {
            Id = "old-youtube-event", Platform = ChatPlatforms.YouTube, Channel = "youtube_UC-owner",
            Kind = StreamEventKinds.Membership, DisplayName = "Viewer", Timestamp = DateTimeOffset.UtcNow
        });
        if (replacement) Round9ApplyYouTube(vm, "UC-new");
        else await vm.DisconnectYouTubeCommand.ExecuteAsync(null);
        await Round9PumpAsync();
        Assert.Empty(vm.StreamEvents);
    }

    [AvaloniaTheory]
    [InlineData("disconnected")]
    [InlineData("disposed")]
    [InlineData("replacement")]
    [InlineData("active")]
    public async Task LifecycleEventAuditQueuedYouTubeDeletionOnlyAffectsCurrentAccount(string mode)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Round9ApplyYouTube(vm);
        var item = new ChatMessageItemViewModel(new ChatMessage
        {
            Id = "youtube-message", Platform = ChatPlatforms.YouTube, Channel = "youtube_UC-owner",
            UserLogin = "viewer", DisplayName = "Viewer", Text = "Audit message", Timestamp = DateTimeOffset.UtcNow
        }, fixture.ImageCache, vm.Texts, owner: vm);
        vm.Messages.Add(item);
        Round9Invoke(vm, "OnYouTubeMessageDeleted", new YouTubeMessageDeletedEventArgs("youtube-message"));
        if (mode == "disconnected") await vm.DisconnectYouTubeCommand.ExecuteAsync(null);
        else if (mode == "disposed") await vm.DisposeAsync();
        else if (mode == "replacement") Round9ApplyYouTube(vm, "UC-new");
        await Round9PumpAsync();
        Assert.Equal(mode == "active" ? ChatMessageModerationState.Deleted : ChatMessageModerationState.None,
            item.ModerationState);
    }

    [AvaloniaFact]
    public async Task LifecycleEventAuditCurrentYouTubeStreamEventAndAnonymousTwitchEventAreKept()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        Round9ApplyYouTube(vm);
        foreach (var platform in new[] { ChatPlatforms.YouTube, ChatPlatforms.Twitch })
        {
            vm.EnqueueStreamEventForTesting(new StreamEvent
            {
                Id = "current-" + platform, Platform = platform,
                Channel = platform == ChatPlatforms.YouTube ? "youtube_UC-owner" : "audit",
                Kind = StreamEventKinds.Subscription, DisplayName = "Viewer", Timestamp = DateTimeOffset.UtcNow
            });
        }
        await Round9PumpAsync();
        Assert.Equal(2, vm.StreamEvents.Count);
    }

    private static DonationAlert Round9Donation(decimal amount) =>
        new("audit-donation", "Viewer", "Audit donation", amount, "RUB", DateTimeOffset.UtcNow);

    private static YouTubeAuthSession Round9ApplyYouTube(MainWindowViewModel vm, string channel = "UC-owner")
    {
        var session = new YouTubeAuthSession("test-access", "test-refresh", "client", channel, "Owner", "@owner",
            null, [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        Round9InvokeSession(vm, "ApplyYouTubeSession", session);
        return session;
    }

    private static void Round9InvokeSession(MainWindowViewModel vm, string method, object session) =>
        typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [session]);

    private static void Round9Invoke(MainWindowViewModel vm, string method, EventArgs args) =>
        typeof(MainWindowViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [null, args]);

    private static T Round9Field<T>(MainWindowViewModel vm, string field) =>
        (T)typeof(MainWindowViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    private static async Task Round9PumpAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
