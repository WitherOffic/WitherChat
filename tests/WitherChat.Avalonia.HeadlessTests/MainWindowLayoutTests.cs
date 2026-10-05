using Avalonia;
using Avalonia.Animation;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.VisualTree;
using SkiaSharp;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task TwitchChatClearRemovesOnlyTargetHistoryAndRejectsLateOldMessages()
    {
        var chatClient = new RecordingChatClient();
        await using var fixture = new WindowFixture(chatClient);
        var viewModel = fixture.ViewModel;
        const string targetChannel = "target_channel";
        var clearedAt = DateTimeOffset.UtcNow;
        viewModel.Channel = targetChannel;

        ChatMessage CreateMessage(
            string id,
            string channel,
            DateTimeOffset timestamp,
            string platform = ChatPlatforms.Twitch) => new()
            {
                Id = id,
                Channel = channel,
                UserId = "viewer-id",
                UserLogin = "viewer",
                DisplayName = "Viewer",
                Text = id,
                Timestamp = timestamp,
                Platform = platform,
                Parts = [ChatMessagePart.PlainText(id)]
            };

        ChatMessageItemViewModel CreateItem(ChatMessage message) => new(
            message,
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);

        var targetOld = CreateItem(CreateMessage(
            "target-old", targetChannel, clearedAt.AddSeconds(-2)));
        var targetNew = CreateItem(CreateMessage(
            "target-new", targetChannel, clearedAt.AddSeconds(2)));
        var otherOld = CreateItem(CreateMessage(
            "other-old", "other_channel", clearedAt.AddSeconds(-2)));
        var youTubeOld = CreateItem(CreateMessage(
            "youtube-old", targetChannel, clearedAt.AddSeconds(-2), ChatPlatforms.YouTube));
        var initial = new[] { targetOld, targetNew, otherOld, youTubeOld };
        viewModel.Messages.AppendBatch(initial, 100);
        viewModel.VisibleMessages.AppendBatch(initial, 100);
        viewModel.TwitchVisibleMessages.AppendBatch([targetOld, targetNew, otherOld], 100);
        viewModel.YouTubeVisibleMessages.AppendBatch([youTubeOld], 100);
        viewModel.PinnedMessageAuthor = "Old author";
        viewModel.PinnedMessageText = "Old pinned message";

        chatClient.Publish(CreateMessage(
            "queued-target-old", targetChannel, clearedAt.AddSeconds(-1)));
        chatClient.Publish(CreateMessage(
            "queued-other-old", "other_channel", clearedAt.AddSeconds(-1)));
        Assert.Equal(2, viewModel.PendingMessageCount);

        viewModel.ApplyEventSubChatClear(new EventSubChatCleared(
            "target-id", targetChannel, clearedAt));
        viewModel.ProcessPendingMessageBatch();

        Assert.DoesNotContain(viewModel.Messages, item => item.Message.Id == "target-old");
        Assert.DoesNotContain(viewModel.Messages, item => item.Message.Id == "queued-target-old");
        Assert.Contains(viewModel.Messages, item => item.Message.Id == "target-new");
        Assert.Contains(viewModel.Messages, item => item.Message.Id == "other-old");
        Assert.Contains(viewModel.Messages, item => item.Message.Id == "queued-other-old");
        Assert.Contains(viewModel.Messages, item => item.Message.Id == "youtube-old");
        Assert.Empty(viewModel.PinnedMessageAuthor);
        Assert.Empty(viewModel.PinnedMessageText);

        chatClient.Publish(CreateMessage(
            "late-old-target", targetChannel, clearedAt.AddMilliseconds(-1)));
        chatClient.Publish(CreateMessage(
            "fresh-target", targetChannel, clearedAt.AddSeconds(3)));
        viewModel.ProcessPendingMessageBatch();

        Assert.DoesNotContain(viewModel.Messages, item => item.Message.Id == "late-old-target");
        Assert.Contains(viewModel.Messages, item => item.Message.Id == "fresh-target");
    }

    [AvaloniaFact]
    public async Task ChatViewSwitchesBetweenCombinedAndSplitPlatformLists()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        const string twitchChannel = "audit";
        const string youTubeChannel = "youtube_UC-owner";
        viewModel.Channel = string.Empty;
        var youTubeSession = new YouTubeAuthSession(
            "access", "refresh", "client", "UC-owner", "Own channel", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        typeof(MainWindowViewModel)
            .GetMethod("ApplyYouTubeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [youTubeSession]);
        Assert.True(viewModel.HasActiveChannel);
        Assert.True(viewModel.ShowMessageList);
        Assert.True(viewModel.ShowReadOnlyComposerNotice);
        typeof(YouTubeLiveChatClient)
            .GetProperty(nameof(YouTubeLiveChatClient.CurrentChannel))!
            .SetValue(fixture.YouTubeLiveChatClient, youTubeChannel);
        viewModel.Channel = twitchChannel;
        viewModel.IsYouTubeLiveConnected = true;

        var twitchItem = new ChatMessageItemViewModel(
            new ChatMessage
            {
                Id = "twitch-split-message",
                Channel = twitchChannel,
                UserLogin = "twitch_viewer",
                DisplayName = "Twitch viewer",
                Text = "Twitch message",
                Timestamp = DateTimeOffset.UtcNow,
                Parts = [ChatMessagePart.PlainText("Twitch message")]
            },
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);
        var youTubeItem = new ChatMessageItemViewModel(
            new ChatMessage
            {
                Id = "youtube-split-message",
                Channel = youTubeChannel,
                UserLogin = "UC-viewer",
                DisplayName = "YouTube viewer",
                Text = "YouTube message",
                Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(1),
                Platform = ChatPlatforms.YouTube,
                Badges = [new ChatBadge("youtube", "1", Title: "YouTube")],
                Parts = [ChatMessagePart.PlainText("YouTube message")]
            },
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);
        viewModel.Messages.Add(twitchItem);
        viewModel.Messages.Add(youTubeItem);
        typeof(MainWindowViewModel)
            .GetMethod("RebuildVisibleMessages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);

        Assert.Equal(2, viewModel.VisibleMessages.Count);
        Assert.Same(twitchItem, Assert.Single(viewModel.TwitchVisibleMessages));
        Assert.Same(youTubeItem, Assert.Single(viewModel.YouTubeVisibleMessages));

        viewModel.IsSplitChatView = true;
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(80);

        var combinedList = window.FindControl<ListBox>("MessagesList");
        var splitGrid = AssertControl<Grid>(window, "SplitChatGrid");
        var twitchList = window.FindControl<ListBox>("TwitchMessagesList");
        var youTubeList = window.FindControl<ListBox>("YouTubeMessagesList");
        Assert.NotNull(twitchList);
        Assert.NotNull(youTubeList);
        Assert.NotNull(combinedList);
        Assert.False(combinedList!.IsVisible);
        Assert.True(splitGrid.IsVisible);
        Assert.Equal(1, twitchList!.ItemCount);
        Assert.Equal(1, youTubeList!.ItemCount);
        Assert.Contains(
            twitchList.GetVisualDescendants().OfType<RichChatTextBlock>(),
            control => ReferenceEquals(control.DataContext, twitchItem));
        Assert.Contains(
            youTubeList.GetVisualDescendants().OfType<RichChatTextBlock>(),
            control => ReferenceEquals(control.DataContext, youTubeItem));
        Assert.Contains(
            youTubeList.GetVisualDescendants().OfType<Grid>(),
            control => AutomationProperties.GetName(control) == "YouTube");

        viewModel.IsYouTubeLiveConnected = false;
        typeof(MainWindowViewModel)
            .GetMethod("RebuildVisibleMessages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);
        await Task.Delay(40);
        Assert.True(splitGrid.IsVisible);
        Assert.Same(youTubeItem, Assert.Single(viewModel.YouTubeVisibleMessages));

        viewModel.ReduceMotion = true;
        viewModel.IsCompactMode = true;
        await Task.Delay(40);
        Assert.True(combinedList.IsVisible);
        Assert.False(splitGrid.IsVisible);
        Assert.True(viewModel.IsSplitChatView);

        viewModel.IsCompactMode = false;
        viewModel.ReduceMotion = false;
        viewModel.ShowCombinedChatViewCommand.Execute(null);
        await Task.Delay(40);
        Assert.True(combinedList.IsVisible);
        Assert.False(splitGrid.IsVisible);

        viewModel.SelectedSettingsSection = SettingsSection.Chat;
        viewModel.IsSettingsOpen = true;
        await Task.Delay(80);
        var settingsCombined = AssertControl<RadioButton>(window, "SettingsCombinedChatViewButton");
        var settingsSplit = AssertControl<RadioButton>(window, "SettingsSplitChatViewButton");
        Assert.True(settingsCombined.IsVisible);
        Assert.True(settingsSplit.IsVisible);
        Assert.True(settingsCombined.IsChecked);
        viewModel.ShowSplitChatViewCommand.Execute(null);
        Assert.True(settingsSplit.IsChecked);
    }

    [AvaloniaFact]
    public async Task CompactModeHidesReplyPreviewAndKeepsTheMessageRowSimple()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        const string parentText =
            "Это длинное исходное сообщение, которое не помещается в компактной строке ответа целиком";
        var message = new ChatMessage
        {
            Id = "compact-reply-preview",
            Channel = "audit",
            UserLogin = "viewer",
            DisplayName = "Viewer",
            Text = "Ответ на сообщение",
            Timestamp = DateTimeOffset.UtcNow,
            ReplyParentDisplayName = "parent_viewer",
            ReplyParentText = parentText,
            Parts = [ChatMessagePart.PlainText("Ответ на сообщение")]
        };
        var item = new ChatMessageItemViewModel(
            message,
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);
        viewModel.Channel = message.Channel;
        viewModel.Messages.Add(item);
        viewModel.VisibleMessages.Add(item);
        viewModel.IsCompactMode = true;
        window.MinWidth = 300;
        window.MinHeight = 400;
        window.Show();
        window.Width = 360;
        window.Height = 400;
        window.Measure(new Size(360, 400));
        window.Arrange(new Rect(0, 0, 360, 400));
        await Task.Delay(60);

        var replyHost = AssertControl<Grid>(window, "FullMessageReplyHost");
        Assert.False(replyHost.IsVisible);
        Assert.False(viewModel.ShowFullChatMetadata);
        Assert.Contains(
            window.GetVisualDescendants().OfType<RichChatTextBlock>(),
            control => ReferenceEquals(control.DataContext, item) && control.UseCompactEmotes);
    }

    [Fact]
    public void CompactBadgesKeepOnlyYouTubeAndOneSubscriptionBadge()
    {
        using var imageCache = new ChatImageCache(new MissingImageHandler());
        var item = new ChatMessageItemViewModel(
            new ChatMessage
            {
                Id = "compact-badges",
                Channel = "audit",
                UserLogin = "viewer",
                DisplayName = "Viewer",
                Text = "Message",
                Timestamp = DateTimeOffset.UtcNow,
                Platform = ChatPlatforms.YouTube,
                Badges =
                [
                    new ChatBadge("moderator", "1"),
                    new ChatBadge("youtube", "1", Title: "YouTube"),
                    new ChatBadge("subscriber", "12"),
                    new ChatBadge("founder", "0"),
                    new ChatBadge("vip", "1")
                ]
            },
            imageCache,
            new UiText());

        Assert.Equal(["YT", "SUB"], item.CompactBadges.Select(badge => badge.Label));
        Assert.True(item.CompactBadges[0].IsYouTube);
        Assert.True(item.CompactBadges[1].IsSubscription);
        Assert.DoesNotContain(item.CompactBadges, badge => badge.SetId is "moderator" or "vip" or "founder");
        Assert.True(item.Badges.Single(badge => badge.SetId == "moderator").IsModerator);
        Assert.True(item.Badges.Single(badge => badge.SetId == "vip").IsVip);
        Assert.True(item.Badges.Single(badge => badge.SetId == "founder").IsSubscription);
    }

    [AvaloniaFact]
    public async Task ChatLinksUseShortPurpleLabelsAndKeepFullAddressInTooltip()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        const string telegramUrl = "https://t.me/recrent?start=from_chat";
        const string genericUrl = "https://news.example.com/articles/very-long-address?source=twitch";
        const string vkUrl = "vk.com/recrent";
        var message = new ChatMessage
        {
            Id = "chat-links",
            Channel = "audit",
            UserLogin = "stream_elements",
            DisplayName = "StreamElements",
            Text = "Telegram " + telegramUrl + " и VK " + vkUrl + " и новости " + genericUrl +
                   " email user@example.com",
            Timestamp = DateTimeOffset.UtcNow,
            Parts = [ChatMessagePart.PlainText(
                "Telegram " + telegramUrl + " и VK " + vkUrl + " и новости " + genericUrl +
                " email user@example.com")]
        };
        var item = new ChatMessageItemViewModel(
            message,
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);
        viewModel.Channel = message.Channel;
        viewModel.Messages.Add(item);
        viewModel.VisibleMessages.Add(item);
        Uri? requestedUri = null;
        viewModel.OpenUriRequested += (_, eventArgs) => requestedUri = eventArgs.Value;

        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(60);

        var linkButtons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(control => AutomationProperties.GetName(control) is
                var name && name is not null &&
                (name == telegramUrl || name == vkUrl || name == genericUrl))
            .ToArray();
        Assert.Equal(3, linkButtons.Length);
        var telegramButton = Assert.Single(
            linkButtons,
            control => control.Content is TextBlock { Text: "Telegram · t.me" });
        var genericButton = Assert.Single(
            linkButtons,
            control => control.Content is TextBlock { Text: "news.example.com" });
        var vkButton = Assert.Single(
            linkButtons,
            control => control.Content is TextBlock { Text: "VK · vk.com" });
        var telegramTooltip = Assert.IsType<TextBlock>(ToolTip.GetTip(telegramButton));
        var genericTooltip = Assert.IsType<TextBlock>(ToolTip.GetTip(genericButton));
        Assert.Equal(telegramUrl, telegramTooltip.Text);
        Assert.Equal(genericUrl, genericTooltip.Text);
        Assert.Equal(vkUrl, Assert.IsType<TextBlock>(ToolTip.GetTip(vkButton)).Text);
        Assert.Equal(420, telegramTooltip.MaxWidth);
        Assert.Equal(TextWrapping.Wrap, telegramTooltip.TextWrapping);
        Assert.Equal(
            Color.Parse("#B69CFF"),
            Assert.IsType<SolidColorBrush>(((TextBlock)telegramButton.Content!).Foreground).Color);

        vkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(20);
        Assert.Equal(new Uri("https://vk.com/recrent"), requestedUri);
    }

    [AvaloniaFact]
    public async Task SupportButtonsWalletsAndViewerLinksDispatchOnlySafeTargets()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        var openedUris = new List<Uri>();
        var copiedValues = new List<string>();
        viewModel.OpenUriRequested += (_, eventArgs) => openedUris.Add(eventArgs.Value);
        viewModel.CopyTextRequested += (_, eventArgs) => copiedValues.Add(eventArgs.Value);

        window.Show();
        viewModel.ToggleSettingsCommand.Execute(null);
        viewModel.ShowDonateSettingsCommand.Execute(null);
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(40);

        var expectedSupportUris = new[]
        {
            new Uri(SupportLinks.Boosty),
            new Uri(SupportLinks.DonationAlerts),
            new Uri(SupportLinks.Twitch),
            new Uri(SupportLinks.YouTube),
            new Uri(SupportLinks.Steam),
            new Uri(SupportLinks.Telegram)
        };
        foreach (var automationId in new[]
                 {
                     "BoostySupportButton", "DonationAlertsSupportButton", "TwitchSupportButton",
                     "YouTubeSupportButton", "SteamSupportButton", "TelegramSupportButton"
                 })
        {
            var button = AssertControl<Button>(window, automationId);
            Assert.NotNull(button.Command);
            Assert.True(button.Command!.CanExecute(button.CommandParameter));
            button.Command.Execute(button.CommandParameter);
        }
        Assert.Equal(expectedSupportUris, openedUris);

        foreach (var automationId in new[] { "TronWalletButton", "TonWalletButton", "BscWalletButton" })
        {
            var button = AssertControl<Button>(window, automationId);
            Assert.NotNull(button.Command);
            Assert.True(button.Command!.CanExecute(button.CommandParameter));
            button.Command.Execute(button.CommandParameter);
        }
        Assert.Equal(
            [SupportLinks.UsdtTrc20, SupportLinks.UsdtTon, SupportLinks.UsdtBsc],
            copiedValues);

        viewModel.OpenSupportLinkCommand.Execute("http://example.test");
        viewModel.OpenSupportLinkCommand.Execute("file:///C:/Windows/System32/calc.exe");
        viewModel.OpenChatLinkCommand.Execute(new Uri("ftp://example.test/file"));
        Assert.Equal(expectedSupportUris, openedUris);

        var twitchItem = CreateViewerLinkItem(
            fixture,
            platform: ChatPlatforms.Twitch,
            userLogin: "@viewer_name",
            userId: "twitch-id");
        viewModel.OpenUserOnTwitchCommand.Execute(twitchItem);
        Assert.Equal(new Uri("https://www.twitch.tv/viewer_name"), openedUris[^1]);

        viewModel.OpenModerationUserOnTwitchCommand.Execute(" @moderated_viewer ");
        Assert.Equal(new Uri("https://www.twitch.tv/moderated_viewer"), openedUris[^1]);

        var youTubeItem = CreateViewerLinkItem(
            fixture,
            platform: ChatPlatforms.YouTube,
            userLogin: "Viewer",
            userId: "UC_test-channel");
        viewModel.OpenUserOnTwitchCommand.Execute(youTubeItem);
        Assert.Equal(new Uri("https://www.youtube.com/channel/UC_test-channel"), openedUris[^1]);

        var youTubeItemWithoutLogin = CreateViewerLinkItem(
            fixture,
            platform: ChatPlatforms.YouTube,
            userLogin: "",
            userId: "UC_id-without-login");
        viewModel.OpenUserOnTwitchCommand.Execute(youTubeItemWithoutLogin);
        Assert.Equal(new Uri("https://www.youtube.com/channel/UC_id-without-login"), openedUris[^1]);

        var countBeforeMissingYouTubeId = openedUris.Count;
        viewModel.OpenUserOnTwitchCommand.Execute(CreateViewerLinkItem(
            fixture,
            platform: ChatPlatforms.YouTube,
            userLogin: "Viewer",
            userId: ""));
        Assert.Equal(countBeforeMissingYouTubeId, openedUris.Count);
    }

    private static ChatMessageItemViewModel CreateViewerLinkItem(
        WindowFixture fixture,
        string platform,
        string userLogin,
        string userId) =>
        new(
            new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Channel = "audit",
                UserLogin = userLogin,
                UserId = userId,
                DisplayName = "Viewer",
                Text = "Message",
                Timestamp = DateTimeOffset.UtcNow,
                Platform = platform,
                Parts = [ChatMessagePart.PlainText("Message")]
            },
            fixture.ImageCache,
            fixture.ViewModel.Texts,
            owner: fixture.ViewModel);

    [AvaloniaFact]
    public async Task TwitchBttvAndSevenTvEmotesRemainCompositedInNormalAndCompactModes()
    {
        await using var fixture = new WindowFixture();
        using var imageCache = new ChatImageCache(new MissingImageHandler());
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        viewModel.Channel = "audit";
        viewModel.ReduceMotion = true;
        var catalog = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal)
        {
            ["BASE"] = new(
                "seven-base",
                "BASE",
                new Uri("https://cdn.7tv.app/emote/seven-base/2x.png"),
                ThirdPartyEmoteProviders.SevenTv,
                SourceWidth: 64,
                SourceHeight: 64),
            ["OVERLAY"] = new(
                "seven-overlay",
                "OVERLAY",
                new Uri("https://cdn.7tv.app/emote/seven-overlay/2x.gif"),
                ThirdPartyEmoteProviders.SevenTv,
                IsZeroWidth: true,
                SourceWidth: 64,
                SourceHeight: 64),
            ["OMEGALUL"] = new(
                "bttv-emote",
                "OMEGALUL",
                new Uri("https://cdn.betterttv.net/emote/bttv-emote/2x"),
                ThirdPartyEmoteProviders.Bttv,
                SourceWidth: 56,
                SourceHeight: 56)
        };
        var message = new ChatMessage
        {
            Id = "provider-audit",
            Channel = "audit",
            UserLogin = "tester",
            DisplayName = "Tester",
            Text = "Kappa BASE OVERLAY OMEGALUL",
            Timestamp = DateTimeOffset.UtcNow,
            Badges =
            [
                new ChatBadge("moderator", "1", Title: "Moderator"),
                new ChatBadge("subscriber", "12", Title: "Subscriber")
            ],
            Parts = TwitchEmoteParser.Parse("Kappa BASE OVERLAY OMEGALUL", "25:0-4")
        };
        var item = new ChatMessageItemViewModel(
            message,
            imageCache,
            viewModel.Texts,
            thirdPartyCatalog: catalog,
            owner: viewModel);
        viewModel.Messages.Add(item);
        viewModel.VisibleMessages.Add(item);

        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        using var renderedFrame = window.CaptureRenderedFrame();

        var richMessage = AssertProviderComposition(window, item);
        Assert.False(richMessage.UseCompactEmotes);
        Assert.All(
            richMessage.Inlines!.OfType<InlineUIContainer>(),
            container => Assert.Equal(28, Assert.IsType<Grid>(container.Child).Height));
        item.ApplyPresentationSettings(true, false, true, catalog);
        Assert.DoesNotContain(item.Parts, part => ThirdPartyEmoteProviders.IsBttv(part.Provider));
        Assert.Contains(item.Parts, part => part.IsZeroWidth && part.Provider == ThirdPartyEmoteProviders.SevenTv);
        item.ApplyPresentationSettings(true, true, true, catalog);

        var compactButton = AssertControl<Button>(window, "CompactModeButton");
        compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => viewModel.IsCompactMode &&
                  compactButton.IsEnabled &&
                  !viewModel.IsMessageRenderingSuspended,
            TimeSpan.FromSeconds(2));
        richMessage = AssertProviderComposition(window, item);
        Assert.True(richMessage.UseCompactEmotes);
        await WaitForAsync(
            () => richMessage.Inlines!.OfType<InlineUIContainer>()
                .All(container => Assert.IsType<Grid>(container.Child).Height == 22),
            TimeSpan.FromSeconds(1));
        Assert.All(
            richMessage.Inlines!.OfType<InlineUIContainer>(),
            container => Assert.Equal(22, Assert.IsType<Grid>(container.Child).Height));
        var timestamp = Assert.Single(
            window.GetVisualDescendants().OfType<TextBlock>(),
            control => ReferenceEquals(control.DataContext, item) && control.Text == item.TimeText);
        var fullBadges = Assert.Single(
            window.GetVisualDescendants().OfType<ItemsControl>(),
            control => ReferenceEquals(control.DataContext, item) && ReferenceEquals(control.ItemsSource, item.Badges));
        var compactBadges = Assert.Single(
            window.GetVisualDescendants().OfType<ItemsControl>(),
            control => ReferenceEquals(control.DataContext, item) &&
                       ReferenceEquals(control.ItemsSource, item.CompactBadges));
        var compactBadgeHost = AssertControl<Grid>(window, "CompactMessageBadgeHost");
        Assert.False(timestamp.IsVisible);
        Assert.False(fullBadges.IsVisible);
        Assert.Empty(fullBadges.GetVisualDescendants().OfType<TextBlock>());
        Assert.NotEmpty(fullBadges.GetVisualDescendants()
            .OfType<global::Avalonia.Controls.Shapes.Path>());
        Assert.True(compactBadgeHost.IsVisible);
        Assert.True(compactBadges.IsVisible);
        Assert.Equal("SUB", Assert.Single(item.CompactBadges).Label);
        Assert.Empty(compactBadges.GetVisualDescendants().OfType<TextBlock>());
        Assert.NotEmpty(compactBadges.GetVisualDescendants()
            .OfType<global::Avalonia.Controls.Shapes.Path>());
        viewModel.ShowBadges = false;
        Assert.False(compactBadgeHost.IsVisible);
        viewModel.ShowBadges = true;
        Assert.True(compactBadgeHost.IsVisible);
    }

    [AvaloniaFact]
    public async Task MessageActionsExposeRecentHistoryAndCustomModerationWithoutSelection()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        viewModel.ReduceMotion = false;
        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "account_owner",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var applySession = typeof(MainWindowViewModel).GetMethod(
            "ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySession);
        applySession!.Invoke(viewModel, [session]);
        viewModel.Channel = "wither_101";
        viewModel.CanModerate = true;

        var message = new ChatMessage
        {
            Id = "context-message",
            Channel = "wither_101",
            UserId = "viewer-id",
            UserLogin = "viewer",
            DisplayName = "Viewer",
            Text = "Kappa KEKW",
            Timestamp = DateTimeOffset.UtcNow,
            Parts = TwitchEmoteParser.Parse("Kappa KEKW", "25:0-4")
        };
        var item = new ChatMessageItemViewModel(
            message,
            fixture.ImageCache,
            viewModel.Texts,
            thirdPartyCatalog: new Dictionary<string, ThirdPartyEmote>
            {
                ["KEKW"] = new(
                    "7tv-id",
                    "KEKW",
                    new Uri("https://cdn.7tv.app/emote/7tv-id/2x.png"),
                    "7TV")
            },
            owner: viewModel);
        viewModel.Messages.Add(item);
        viewModel.VisibleMessages.Add(item);

        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        var messagesList = window.FindControl<ListBox>("MessagesList");
        Assert.NotNull(messagesList);
        messagesList!.SelectedItem = item;
        Assert.Null(messagesList.SelectedItem);
        Assert.Equal(3, item.Parts.Count);
        Assert.Equal(ChatMessagePartKind.Emote, item.Parts[0].Kind);
        Assert.Equal("Twitch", item.Parts[0].Provider);
        Assert.Equal(ChatMessagePartKind.Emote, item.Parts[2].Kind);
        Assert.Equal("7TV", item.Parts[2].Provider);
        var richMessage = Assert.Single(
            window.GetVisualDescendants().OfType<RichChatTextBlock>(),
            control => ReferenceEquals(control.DataContext, item));
        Assert.NotNull(richMessage.Inlines);
        Assert.NotEmpty(richMessage.Inlines);

        var messageRow = window.GetLogicalDescendants()
            .OfType<Grid>()
            .First(control => ReferenceEquals(control.DataContext, item) && control.ContextMenu is not null);
        var contextMenu = messageRow.ContextMenu!;
        var messageContainer = Assert.Single(
            messagesList.GetVisualDescendants().OfType<ListBoxItem>(),
            control => ReferenceEquals(control.DataContext, item));
        Assert.True(messageRow.Bounds.Width >= messageContainer.Bounds.Width - 20);
        var rowOrigin = messageRow.TranslatePoint(default, window);
        Assert.NotNull(rowOrigin);
        var emptyRowPoint = rowOrigin!.Value + new Vector(
            messageRow.Bounds.Width - 8,
            messageRow.Bounds.Height / 2);
        window.MouseDown(emptyRowPoint, MouseButton.Right);
        window.MouseUp(emptyRowPoint, MouseButton.Right);
        await Task.Delay(60);
        Assert.True(contextMenu.IsOpen);
        await WaitForAsync(() => item.ProfileImageResource is not null, TimeSpan.FromSeconds(1));
        Assert.NotNull(item.ProfileImageResource);
        Assert.True(contextMenu.Bounds.Width >= 325);
        Assert.Equal(14, contextMenu.CornerRadius.TopLeft);
        var viewerProfileCard = AssertControl<Border>(contextMenu, "MessageViewerProfileCard");
        Assert.True(viewerProfileCard.Bounds.Width >= 295);
        Assert.Equal(12, viewerProfileCard.CornerRadius.TopLeft);
        Assert.Contains(
            contextMenu.Items.OfType<MenuItem>(),
            menuItem => Equals(menuItem.Header, viewModel.Texts.CustomTimeout));
        viewModel.ReduceMotion = true;
        contextMenu.Close();
        viewModel.ReduceMotion = false;

        var compactButton = AssertControl<Button>(window, "CompactModeButton");
        compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => viewModel.IsCompactMode &&
                  compactButton.IsEnabled &&
                  !viewModel.IsMessageRenderingSuspended,
            TimeSpan.FromSeconds(2));
        messageRow = window.GetLogicalDescendants()
            .OfType<Grid>()
            .First(control => ReferenceEquals(control.DataContext, item) && control.ContextMenu is not null);
        var compactRowOrigin = messageRow.TranslatePoint(default, window);
        Assert.NotNull(compactRowOrigin);
        var compactEmptyRowPoint = compactRowOrigin!.Value + new Vector(
            messageRow.Bounds.Width - 8,
            messageRow.Bounds.Height / 2);
        window.MouseDown(compactEmptyRowPoint, MouseButton.Right);
        window.MouseUp(compactEmptyRowPoint, MouseButton.Right);
        await Task.Delay(60);
        Assert.True(messageRow.ContextMenu!.IsOpen);
        viewModel.ReduceMotion = true;
        messageRow.ContextMenu.Close();
        viewModel.ReduceMotion = false;

        viewModel.CanModerate = false;
        window.MouseDown(compactEmptyRowPoint, MouseButton.Right);
        window.MouseUp(compactEmptyRowPoint, MouseButton.Right);
        await Task.Delay(60);
        Assert.True(messageRow.ContextMenu.IsOpen);
        Assert.False(FindMenuControl(messageRow.ContextMenu, "MessageModerationSeparator").IsVisible);
        Assert.False(FindMenuControl(messageRow.ContextMenu, "MessageDeleteMenuItem").IsVisible);
        Assert.False(FindMenuControl(messageRow.ContextMenu, "MessageBanMenuItem").IsVisible);
        Assert.False(FindMenuControl(messageRow.ContextMenu, "MessageTimeoutMenuItem").IsVisible);
        Assert.False(FindMenuControl(messageRow.ContextMenu, "MessageCustomTimeoutMenuItem").IsVisible);
        Assert.False(FindMenuControl(messageRow.ContextMenu, "MessageRemovePunishmentMenuItem").IsVisible);
        viewModel.ReduceMotion = true;
        messageRow.ContextMenu.Close();
        viewModel.ReduceMotion = false;
        viewModel.CanModerate = true;

        viewModel.ShowRecentMessagesCommand.Execute(item);
        Assert.True(viewModel.IsRecentMessagesOpen);
        Assert.Single(viewModel.RecentUserMessages);
        Assert.True(AssertControl<Border>(window, "RecentMessagesOverlay").IsVisible);

        viewModel.CloseRecentMessagesCommand.Execute(null);
        viewModel.CustomTimeoutCommand.Execute(item);
        Assert.True(viewModel.IsModerationDialogOpen);
        Assert.Equal(1, viewModel.ModerationDurationMinutes);
    }

    [AvaloniaFact]
    public async Task MessageProfileAvatarFallsBackToPublicTwitchLookupAndIsReused()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        ChatMessageItemViewModel CreateItem(string id) => new(
            new ChatMessage
            {
                Id = id,
                Channel = "wither_101",
                UserId = "1333126195",
                UserLogin = "wither_101",
                DisplayName = "WitheR_101",
                Text = "Test",
                Timestamp = DateTimeOffset.UtcNow,
                Parts = [ChatMessagePart.PlainText("Test")]
            },
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);

        var first = CreateItem("public-profile-1");
        var second = CreateItem("public-profile-2");
        await viewModel.PrepareMessageUserProfileAsync(first);
        await viewModel.PrepareMessageUserProfileAsync(second);

        Assert.NotNull(first.ProfileImageResource);
        Assert.Same(first.ProfileImageResource, second.ProfileImageResource);
    }

    [AvaloniaFact]
    public async Task YouTubeMessagesUseTheSharedModerationMenuDialogAndPanel()
    {
        var handler = new RecordingYouTubeModerationHandler();
        await using var fixture = new WindowFixture(youTubeHandler: handler);
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        window.RequestedThemeVariant = ThemeVariant.Dark;
        var session = new YouTubeAuthSession(
            "access", "refresh", YouTubeApplication.ClientId,
            "UC-owner", "Owner channel", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope, YouTubeAuthService.ModerationScope],
            DateTimeOffset.UtcNow.AddHours(1));
        typeof(MainWindowViewModel)
            .GetMethod("ApplyYouTubeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [session]);
        await fixture.YouTubeLiveChatClient.StartAsync(session, TestContext.Current.CancellationToken);
        await WaitForAsync(() => viewModel.CanModerateYouTube, TimeSpan.FromSeconds(3));
        Assert.True(viewModel.CanModerateYouTube);
        Assert.True(viewModel.CanModerateAny);

        var item = new ChatMessageItemViewModel(
            new ChatMessage
            {
                Id = "youtube-message-1",
                PlatformMessageId = "youtube-api-message-1",
                Channel = "youtube_UC-owner",
                BroadcasterId = "UC-owner",
                UserId = "UC-viewer",
                UserLogin = "UC-viewer",
                DisplayName = "YouTube viewer",
                Text = "Moderate me",
                Timestamp = DateTimeOffset.UtcNow,
                Platform = ChatPlatforms.YouTube,
                Parts = [ChatMessagePart.PlainText("Moderate me")]
            },
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);
        viewModel.Messages.Add(item);
        viewModel.VisibleMessages.Add(item);
        viewModel.YouTubeVisibleMessages.Add(item);
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));

        var messageRow = window.GetLogicalDescendants()
            .OfType<Grid>()
            .First(control => ReferenceEquals(control.DataContext, item) && control.ContextMenu is not null);
        var origin = messageRow.TranslatePoint(default, window);
        Assert.NotNull(origin);
        var point = origin!.Value + new Vector(messageRow.Bounds.Width - 8, messageRow.Bounds.Height / 2);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        await Task.Delay(60);
        var menu = messageRow.ContextMenu!;
        Assert.True(menu.IsOpen);
        Assert.True(FindMenuControl(menu, "MessageModerationSeparator").IsVisible);
        Assert.True(FindMenuControl(menu, "MessageDeleteMenuItem").IsVisible);
        Assert.True(FindMenuControl(menu, "MessageBanMenuItem").IsVisible);
        Assert.True(FindMenuControl(menu, "MessageTimeoutMenuItem").IsVisible);
        menu.Close();

        viewModel.DeleteMessageCommand.Execute(item);
        await WaitForAsync(() => handler.DeletedMessageIds.Contains("youtube-api-message-1"), TimeSpan.FromSeconds(2));
        Assert.Equal(ChatMessageModerationState.Deleted, item.ModerationState);

        viewModel.TimeoutTenMinutesCommand.Execute(item);
        Assert.True(viewModel.IsModerationDialogOpen);
        Assert.False(viewModel.ShowModerationReason);
        viewModel.ConfirmModerationCommand.Execute(null);
        await WaitForAsync(() => viewModel.YouTubeBans.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(ChatMessageModerationState.TimedOut, item.ModerationState);
        Assert.Equal("UC-viewer", viewModel.YouTubeBans[0].Value.UserChannelId);

        viewModel.OpenModerationPanelCommand.Execute(null);
        await WaitForAsync(() => viewModel.IsModerationPanelOpen, TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsYouTubeModerationSelected);
        Assert.True(window.FindControl<Border>("ModerationPanelOverlay")!.IsEffectivelyVisible);
        RenderAndAssert(window, 1100, 760, "moderation-youtube-ru-dark-1100x760.png");
        viewModel.UnbanYouTubeUserCommand.Execute(viewModel.YouTubeBans[0]);
        await WaitForAsync(() => viewModel.YouTubeBans.Count == 0, TimeSpan.FromSeconds(2));
        Assert.Contains("ban-1", handler.RemovedBanIds);
    }

    [AvaloniaFact]
    public async Task LegacyYouTubeSessionShowsExplicitModerationPermissionButton()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var session = new YouTubeAuthSession(
            "access", "refresh", YouTubeApplication.ClientId,
            "UC-owner", "Owner channel", "@owner", null,
            [YouTubeAuthService.ReadOnlyScope], DateTimeOffset.UtcNow.AddHours(1));
        typeof(MainWindowViewModel)
            .GetMethod("ApplyYouTubeSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [session]);
        viewModel.SelectedSettingsSection = SettingsSection.Account;
        viewModel.IsSettingsOpen = true;
        fixture.Window.Show();
        fixture.Window.Measure(new Size(1100, 760));
        fixture.Window.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(40);

        Assert.True(viewModel.RequiresYouTubeModerationReconnect);
        Assert.False(viewModel.HasYouTubeModerationPermission);
        Assert.False(viewModel.CanModerateYouTube);
        var button = AssertControl<Button>(fixture.Window, "EnableYouTubeModerationButton");
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public async Task SignedInAccountOwnsProtectedFirstChannelAndKeepsThreeChannelLimit()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        viewModel.SavedChannels.Clear();
        viewModel.SavedChannels.Add(new ChannelSessionViewModel("viewer_one"));
        viewModel.SavedChannels.Add(new ChannelSessionViewModel("viewer_two"));
        viewModel.SavedChannels.Add(new ChannelSessionViewModel("viewer_three"));

        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "account_owner",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var applySession = typeof(MainWindowViewModel).GetMethod(
            "ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySession);
        applySession!.Invoke(viewModel, [session]);

        Assert.Equal(3, viewModel.SavedChannels.Count);
        var ownChannel = viewModel.SavedChannels[0];
        Assert.Equal("account_owner", ownChannel.Login);
        Assert.True(ownChannel.IsPrimaryAccountChannel);
        Assert.False(ownChannel.CanRemove);
        Assert.False(viewModel.CanAddSavedChannel);
        viewModel.SelectedSavedChannel = ownChannel;
        Assert.False(viewModel.CanRemoveChannel);

        viewModel.SignOutCommand.Execute(null);
        Assert.DoesNotContain(viewModel.SavedChannels, channel => channel.IsPrimaryAccountChannel);
        Assert.Equal(2, viewModel.SavedChannels.Count);
        Assert.True(viewModel.CanAddSavedChannel);
    }

    [AvaloniaFact]
    public async Task StreamStatusRefreshKeepsSavedChannelCardInSyncWhenBroadcastEnds()
    {
        var streamHandler = new SequencedStreamHandler(
            new TwitchStreamStatus(true, 4_482),
            new TwitchStreamStatus(false, 0));
        await using var fixture = new WindowFixture(apiHandler: streamHandler);
        var viewModel = fixture.ViewModel;
        var savedChannel = new ChannelSessionViewModel("yuuechka")
        {
            DisplayName = "yuuechka"
        };
        viewModel.SavedChannels.Clear();
        viewModel.SavedChannels.Add(savedChannel);
        viewModel.Channel = savedChannel.Login;
        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "wither_101",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var refreshStreamStatus = typeof(MainWindowViewModel).GetMethod(
            "RefreshStreamStatusSafeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refreshStreamStatus);

        await Assert.IsAssignableFrom<Task>(refreshStreamStatus!.Invoke(viewModel, [session, savedChannel.Login]));

        Assert.True(viewModel.IsStreamLive);
        Assert.Equal(4_482, viewModel.StreamViewerCount);
        Assert.True(savedChannel.IsLive);
        Assert.Equal(4_482, savedChannel.ViewerCount);

        await Assert.IsAssignableFrom<Task>(refreshStreamStatus.Invoke(viewModel, [session, savedChannel.Login]));

        Assert.False(viewModel.IsStreamLive);
        Assert.Equal(0, viewModel.StreamViewerCount);
        Assert.False(savedChannel.IsLive);
        Assert.Equal(0, savedChannel.ViewerCount);
    }

    [AvaloniaFact]
    public async Task MalformedStreamStatusJsonIsContainedAtTheUiTimerBoundary()
    {
        await using var fixture = new WindowFixture(apiHandler: new MalformedStreamHandler());
        var viewModel = fixture.ViewModel;
        viewModel.Channel = "wither_101";
        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "wither_101",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var refreshStreamStatus = typeof(MainWindowViewModel).GetMethod(
            "RefreshStreamStatusSafeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var exception = await Record.ExceptionAsync(() =>
            Assert.IsAssignableFrom<Task>(refreshStreamStatus!.Invoke(viewModel, [session, "wither_101"])));

        Assert.Null(exception);
        Assert.False(viewModel.IsStreamStatusKnown);
        Assert.False(viewModel.IsStreamLive);
        Assert.Equal(0, viewModel.StreamViewerCount);
    }

    [AvaloniaFact]
    public async Task EmptySavedChannelMetadataResponseIsContainedAtTheUiTimerBoundary()
    {
        await using var fixture = new WindowFixture(apiHandler: new EmptyPublicChannelHandler());
        var savedChannel = new ChannelSessionViewModel("wither_101")
        {
            DisplayName = "Existing name",
            ViewerCount = 42
        };
        fixture.ViewModel.SavedChannels.Clear();
        fixture.ViewModel.SavedChannels.Add(savedChannel);
        var refreshMetadata = typeof(MainWindowViewModel).GetMethod(
            "RefreshSavedChannelMetadataSafeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var exception = await Record.ExceptionAsync(() =>
            Assert.IsAssignableFrom<Task>(refreshMetadata!.Invoke(fixture.ViewModel, null)));

        Assert.Null(exception);
        Assert.Equal("Existing name", savedChannel.DisplayName);
        Assert.Equal(42, savedChannel.ViewerCount);
    }

    [AvaloniaFact]
    public async Task MalformedPinnedMessageJsonIsContainedAtTheUiTimerBoundary()
    {
        await using var fixture = new WindowFixture(apiHandler: new MalformedPinnedMessageHandler());
        var viewModel = fixture.ViewModel;
        viewModel.Channel = "wither_101";
        viewModel.CanModerate = true;
        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "wither_101",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        typeof(MainWindowViewModel)
            .GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, session);
        var refreshPinned = typeof(MainWindowViewModel).GetMethod(
            "RefreshPinnedMessageSafeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var exception = await Record.ExceptionAsync(() =>
            Assert.IsAssignableFrom<Task>(refreshPinned!.Invoke(viewModel, null)));

        Assert.Null(exception);
        Assert.True(viewModel.CanModerate);
    }

    [AvaloniaFact]
    public async Task SignedInChannelLookupFallsBackToExactPublicProfile()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "account_owner",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var applySession = typeof(MainWindowViewModel).GetMethod(
            "ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySession);
        applySession!.Invoke(viewModel, [session]);

        viewModel.OpenConnectPanelCommand.Execute(null);
        viewModel.ConnectPanelChannel = "wither_101";
        await Task.Delay(420);

        Assert.True(viewModel.CanWatchChannel);
        var result = Assert.Single(viewModel.ChannelSearchResults);
        Assert.Equal("WitheR_101", result.DisplayName);
        Assert.Equal(42, result.ViewerCount);
    }

    [AvaloniaFact]
    public async Task ClickingChannelSearchResultConnectsImmediately()
    {
        var chatClient = new RecordingChatClient();
        await using var fixture = new WindowFixture(chatClient);
        var viewModel = fixture.ViewModel;
        viewModel.OpenConnectPanelCommand.Execute(null);
        var result = new ChannelSearchResultViewModel(
            new ChannelSearchResult(
                "channel-id",
                "t2x2",
                "T2x2",
                "https://example.test/t2x2.png",
                "Just Chatting",
                "Live",
                true,
                DateTimeOffset.UtcNow,
                11_134),
            null);

        await viewModel.SelectChannelSearchResultCommand.ExecuteAsync(result);

        Assert.False(viewModel.IsConnectPanelOpen);
        Assert.Equal("t2x2", viewModel.Channel);
        Assert.Contains("t2x2", chatClient.Channels);
        Assert.Equal("t2x2", viewModel.SelectedSavedChannel?.Login);
    }

    [AvaloniaFact]
    public async Task AddingASecondViewerChannelKeepsBothSessionsConnected()
    {
        var chatClient = new RecordingChatClient();
        await using var fixture = new WindowFixture(chatClient);
        var viewModel = fixture.ViewModel;

        viewModel.OpenConnectPanelCommand.Execute(null);
        await viewModel.SelectChannelSearchResultCommand.ExecuteAsync(CreateSearchResult("first_viewer"));
        viewModel.StartAddChannelCommand.Execute(null);
        await viewModel.SelectChannelSearchResultCommand.ExecuteAsync(CreateSearchResult("second_viewer"));

        Assert.Equal(2, viewModel.SavedChannels.Count);
        Assert.Equal(["first_viewer", "second_viewer"], viewModel.SavedChannels.Select(item => item.Login));
        Assert.Contains("first_viewer", chatClient.Channels);
        Assert.Contains("second_viewer", chatClient.Channels);
        Assert.Equal("second_viewer", viewModel.SelectedSavedChannel?.Login);
        Assert.False(viewModel.IsConnectPanelOpen);
    }

    [Fact]
    public void CompactTargetRemainsReadableAtRetinaScaling()
    {
        var target = MainWindow.GetCompactTargetMetrics(2);

        Assert.Equal(new Size(360, 400), target.LogicalSize);
        Assert.Equal(new PixelSize(720, 800), target.PixelSize);
    }

    [AvaloniaFact]
    public async Task CompactMorphSuspendsRenderingForLargeChatHistory()
    {
        var chatClient = new RecordingChatClient();
        await using var fixture = new WindowFixture(chatClient);
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        viewModel.Channel = "witherchat";
        viewModel.MessageLimit = 10_000;
        viewModel.ReduceMotion = false;
        var messages = Enumerable.Range(0, 10_000)
            .Select(index => new ChatMessageItemViewModel(
                new ChatMessage
                {
                    Id = "compact-load-" + index,
                    Channel = "witherchat",
                    UserLogin = "viewer",
                    DisplayName = "Viewer",
                    Text = "A long chat message used to verify compact mode performance " + index,
                    Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(index),
                    Parts =
                    [
                        ChatMessagePart.PlainText(
                            "A long chat message used to verify compact mode performance " + index)
                    ]
                },
                fixture.ImageCache,
                viewModel.Texts,
                owner: viewModel))
            .ToArray();
        viewModel.Messages.AppendBatch(messages, 10_000);
        viewModel.VisibleMessages.AppendBatch(messages, 10_000);

        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        using var renderedFrame = window.CaptureRenderedFrame();
        var messageList = window.FindControl<ListBox>("MessagesList");
        Assert.NotNull(messageList);
        Assert.True(messageList.IsVisible);
        var scrollViewer = messageList.GetVisualDescendants().OfType<ScrollViewer>().Single();
        scrollViewer.ScrollToEnd();
        viewModel.SetFollowingLatest(true);
        Assert.True(viewModel.IsFollowingLatest);

        var compactButton = AssertControl<Button>(window, "CompactModeButton");
        compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => viewModel.IsMessageRenderingSuspended,
            TimeSpan.FromSeconds(1));

        Assert.True(viewModel.IsMessageRenderingSuspended);
        Assert.True(viewModel.IsMessageBatchProcessingSuspended);
        Assert.False(messageList.IsVisible);
        Assert.Equal(10_000, viewModel.VisibleMessages.Count);

        var queuedMessage = new ChatMessage
        {
            Id = "queued-during-compact-morph",
            Channel = "witherchat",
            UserLogin = "viewer",
            DisplayName = "Viewer",
            Text = "Queued while the compact animation is running",
            Timestamp = DateTimeOffset.UtcNow,
            Parts = [ChatMessagePart.PlainText("Queued while the compact animation is running")]
        };
        Assert.True(chatClient.HasMessageReceiver);
        chatClient.Publish(queuedMessage);
        Assert.Equal(1, viewModel.PendingMessageCount);
        viewModel.ProcessPendingMessageBatch();
        Assert.Equal(1, viewModel.PendingMessageCount);
        Assert.DoesNotContain(
            viewModel.Messages,
            item => string.Equals(item.Message.Id, queuedMessage.Id, StringComparison.Ordinal));

        await WaitForAsync(
            () => viewModel.IsCompactMode &&
                  !viewModel.IsMessageRenderingSuspended &&
                  compactButton.IsEnabled &&
                  Math.Abs(window.Bounds.Width - 360) < 1,
            TimeSpan.FromSeconds(3));

        Assert.True(viewModel.IsCompactMode);
        Assert.False(viewModel.IsMessageRenderingSuspended);
        Assert.False(viewModel.IsMessageBatchProcessingSuspended);
        Assert.True(messageList.IsVisible);
        viewModel.ProcessPendingMessageBatch();
        Assert.Contains(
            viewModel.Messages,
            item => string.Equals(item.Message.Id, queuedMessage.Id, StringComparison.Ordinal));
        Assert.Equal(10_001, viewModel.VisibleMessages.Count);
        Assert.Equal(360, window.Bounds.Width, 1);
        Assert.Equal(400, window.Bounds.Height, 1);

        var stoppedFollowingDuringFeed = false;
        var visibleResetCount = 0;
        PropertyChangedEventHandler followingObserver = (_, changed) =>
        {
            if (changed.PropertyName == nameof(MainWindowViewModel.IsFollowingLatest) &&
                !viewModel.IsFollowingLatest)
            {
                stoppedFollowingDuringFeed = true;
            }
        };
        NotifyCollectionChangedEventHandler visibleMessagesObserver = (_, changed) =>
        {
            if (changed.Action == NotifyCollectionChangedAction.Reset)
            {
                visibleResetCount++;
            }
        };
        viewModel.PropertyChanged += followingObserver;
        viewModel.VisibleMessages.CollectionChanged += visibleMessagesObserver;
        for (var index = 0; index < 160; index++)
        {
            var liveMessage = queuedMessage with
            {
                Id = "compact-live-" + index,
                Text = index % 2 == 0
                    ? "Short live message " + index
                    : "A taller live message that wraps in compact mode and must not move the viewport upward " + index,
                Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(index)
            };
            chatClient.Publish(liveMessage);
            if (index % 8 == 7)
            {
                viewModel.ProcessPendingMessageBatch();
                await Task.Delay(12);
            }
        }
        await Task.Delay(120);
        viewModel.PropertyChanged -= followingObserver;
        viewModel.VisibleMessages.CollectionChanged -= visibleMessagesObserver;
        Assert.False(stoppedFollowingDuringFeed);
        Assert.Equal(0, visibleResetCount);
        Assert.True(viewModel.IsFollowingLatest);
        Assert.Equal(10_008, viewModel.VisibleMessages.Count);
        Assert.False(AssertControl<Button>(window, "JumpToLatestButton").IsVisible);
        var latestMessage = viewModel.VisibleMessages[^1];
        await WaitForAsync(
            () => scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y <= 1,
            TimeSpan.FromSeconds(1));
        await WaitForAsync(
            () =>
            {
                var latestContainer = messageList
                    .GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .FirstOrDefault(item => ReferenceEquals(item.DataContext, latestMessage));
                var latestOrigin = latestContainer?.TranslatePoint(default, scrollViewer);
                return latestContainer is not null &&
                       latestOrigin is { } origin &&
                       origin.Y + latestContainer.Bounds.Height <= scrollViewer.Viewport.Height + 2;
            },
            TimeSpan.FromSeconds(2));
        var maximumOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        window.BeginMessageHistoryNavigation();
        scrollViewer.Offset = new Vector(0, Math.Max(0, maximumOffset - 240));
        await WaitForAsync(() => !viewModel.IsFollowingLatest, TimeSpan.FromSeconds(1));
        await Task.Delay(80);
        Assert.False(viewModel.IsFollowingLatest);
        var jumpToLatestButton = AssertControl<Button>(window, "JumpToLatestButton");
        Assert.True(jumpToLatestButton.IsVisible);
        var jumpBackground = Assert.IsAssignableFrom<ISolidColorBrush>(jumpToLatestButton.Background).Color;
        var jumpBorder = Assert.IsAssignableFrom<ISolidColorBrush>(jumpToLatestButton.BorderBrush).Color;
        Assert.InRange(jumpBackground.A, (byte)1, (byte)48);
        Assert.True(jumpBorder.A > jumpBackground.A);
        var chatViewport = AssertControl<Border>(window, "ChatViewportCard");
        var jumpOrigin = jumpToLatestButton.TranslatePoint(default, chatViewport);
        Assert.NotNull(jumpOrigin);
        Assert.True(jumpOrigin.Value.Y > chatViewport.Bounds.Height / 2);
        await Task.Delay(260);

        var firstVisibleItem = messageList.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(item => (Item: item, Origin: item.TranslatePoint(default, scrollViewer)))
            .Where(entry => entry.Origin is { } origin && origin.Y + entry.Item.Bounds.Height > 0)
            .OrderBy(entry => entry.Origin!.Value.Y)
            .First();
        Assert.True(
            firstVisibleItem.Origin!.Value.Y > -firstVisibleItem.Item.Bounds.Height,
            $"The compact viewport lost its first visible message at {firstVisibleItem.Origin.Value.Y:0.##}.");
    }

    [AvaloniaFact]
    public async Task RapidScrollingLargeChatSettlesAndStillEntersCompactMode()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        viewModel.Channel = "witherchat";
        viewModel.ReduceMotion = false;
        var messages = Enumerable.Range(0, 3_000)
            .Select(index => new ChatMessageItemViewModel(
                new ChatMessage
                {
                    Id = "rapid-scroll-" + index,
                    Channel = "witherchat",
                    UserLogin = "viewer",
                    DisplayName = "Viewer",
                    Text = "Rapid scrolling message " + index,
                    Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(index),
                    Parts = [ChatMessagePart.PlainText("Rapid scrolling message " + index)]
                },
                fixture.ImageCache,
                viewModel.Texts,
                owner: viewModel))
            .ToArray();
        viewModel.Messages.AppendBatch(messages, 10_000);
        viewModel.VisibleMessages.AppendBatch(messages, 10_000);

        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        using var renderedFrame = window.CaptureRenderedFrame();
        var messageList = window.FindControl<ListBox>("MessagesList")!;
        var scrollViewer = messageList.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
        scrollViewer.ScrollToEnd();
        await Task.Delay(120);

        var maximumOffset = scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        for (var index = 0; index < 800; index++)
        {
            var progress = index % 2 == 0 ? 0.15 : 0.85;
            scrollViewer.Offset = new Vector(0, maximumOffset * progress);
        }

        viewModel.ShowLatestCommand.Execute(null);
        window.BeginMessageHistoryNavigation();
        scrollViewer.Offset = new Vector(0, 0);
        await Task.Delay(260);
        Assert.False(viewModel.IsFollowingLatest);
        Assert.Equal(0, scrollViewer.Offset.Y, 1);
        Assert.Equal(3_000, viewModel.VisibleMessages.Count);

        for (var index = 0; index < 800; index++)
        {
            viewModel.SetFollowingLatest(false);
            viewModel.ShowLatestCommand.Execute(null);
        }

        await WaitForAsync(
            () => viewModel.IsFollowingLatest &&
                  scrollViewer.Extent.Height -
                  scrollViewer.Viewport.Height -
                  scrollViewer.Offset.Y <= 1,
            TimeSpan.FromSeconds(2));

        var compactButton = AssertControl<Button>(window, "CompactModeButton");
        compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => viewModel.IsCompactMode &&
                  !viewModel.IsMessageRenderingSuspended &&
                  compactButton.IsEnabled,
            TimeSpan.FromSeconds(3));

        Assert.Equal(3_000, viewModel.VisibleMessages.Count);
        Assert.True(messageList.IsVisible);
    }

    [AvaloniaFact]
    public async Task JumpToLatestEnablesFollowingEvenWithoutUnreadMessages()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        var scrollRequested = false;
        viewModel.ScrollToLatestRequested += (_, _) => scrollRequested = true;
        viewModel.Channel = "witherchat";
        viewModel.UnreadCount = 0;

        viewModel.SetFollowingLatest(false);

        Assert.True(viewModel.ShowJumpToLatestButton);
        viewModel.ShowLatestCommand.Execute(null);
        Assert.True(viewModel.IsFollowingLatest);
        Assert.False(viewModel.ShowJumpToLatestButton);
        Assert.True(scrollRequested);

        var messages = Enumerable.Range(0, 120)
            .Select(index => new ChatMessageItemViewModel(
                new ChatMessage
                {
                    Id = "scroll-" + index,
                    Channel = "witherchat",
                    UserLogin = "viewer",
                    DisplayName = "Viewer",
                    Text = "Message " + index,
                    Timestamp = DateTimeOffset.UtcNow.AddSeconds(index),
                    Parts = [ChatMessagePart.PlainText("Message " + index)]
                },
                fixture.ImageCache,
                viewModel.Texts,
                owner: viewModel))
            .ToArray();
        viewModel.Messages.AppendBatch(messages, 200);
        viewModel.VisibleMessages.AppendBatch(messages, 200);
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        using var renderedFrame = window.CaptureRenderedFrame();
        await Task.Delay(120);
        var messageList = window.FindControl<ListBox>("MessagesList")!;
        var scrollViewer = messageList.GetVisualDescendants().OfType<ScrollViewer>().Single();
        Assert.True(
            scrollViewer.Extent.Height > scrollViewer.Viewport.Height,
            $"Expected a scrollable chat, extent={scrollViewer.Extent.Height}, viewport={scrollViewer.Viewport.Height}.");
        window.ScrollMessagesWithWheelForTesting(-1);
        await WaitForAsync(() => viewModel.IsFollowingLatest, TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsFollowingLatest);

        var offsetBeforeWheel = scrollViewer.Offset.Y;
        window.ScrollMessagesWithWheelForTesting(1);
        await WaitForAsync(
            () => !viewModel.IsFollowingLatest && scrollViewer.Offset.Y < offsetBeforeWheel,
            TimeSpan.FromSeconds(1));
        Assert.False(viewModel.IsFollowingLatest);
        var jumpToLatestButton = AssertControl<Button>(window, "JumpToLatestButton");
        Assert.True(jumpToLatestButton.IsVisible);
        Assert.Equal(40, jumpToLatestButton.Width);
        Assert.Equal(40, jumpToLatestButton.Height);
        Assert.Equal(new CornerRadius(20), jumpToLatestButton.CornerRadius);
        Assert.Equal(HorizontalAlignment.Center, jumpToLatestButton.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Center, jumpToLatestButton.VerticalContentAlignment);
        var chatViewport = AssertControl<Border>(window, "ChatViewportCard");
        var jumpOrigin = jumpToLatestButton.TranslatePoint(default, chatViewport);
        Assert.NotNull(jumpOrigin);
        Assert.InRange(jumpOrigin.Value.X, 0, chatViewport.Bounds.Width - jumpToLatestButton.Bounds.Width);
        Assert.InRange(jumpOrigin.Value.Y, 0, chatViewport.Bounds.Height - jumpToLatestButton.Bounds.Height);

        window.ScrollMessagesWithWheelForTesting(-1);
        await WaitForAsync(() => viewModel.IsFollowingLatest, TimeSpan.FromSeconds(1));
        Assert.True(viewModel.IsFollowingLatest);
        Assert.False(AssertControl<Button>(window, "JumpToLatestButton").IsVisible);

        var offsetBeforeMiddleScroll = scrollViewer.Offset.Y;
        var middleScrollPoint = messageList.TranslatePoint(new Point(80, 120), window);
        Assert.NotNull(middleScrollPoint);
        window.MouseDown(middleScrollPoint.Value, MouseButton.Middle);
        await Task.Delay(20);
        var middleScrollIndicator = Assert.IsType<Border>(window.FindControl<Border>("MiddleScrollIndicator"));
        var jumpToLatestContainer = Assert.IsType<Grid>(window.FindControl<Grid>("JumpToLatestContainer"));
        Assert.True(middleScrollIndicator.IsVisible);
        Assert.Equal(0, jumpToLatestContainer.Opacity);
        Assert.False(jumpToLatestContainer.IsHitTestVisible);

        window.MouseDown(middleScrollPoint.Value, MouseButton.Middle);
        await Task.Delay(20);
        Assert.False(middleScrollIndicator.IsVisible);
        Assert.Equal(1, jumpToLatestContainer.Opacity);
        Assert.True(jumpToLatestContainer.IsHitTestVisible);

        window.ScrollMessagesWithMiddleButtonForTesting(-100);
        await WaitForAsync(
            () => !viewModel.IsFollowingLatest && scrollViewer.Offset.Y < offsetBeforeMiddleScroll,
            TimeSpan.FromSeconds(1));
        Assert.True(AssertControl<Button>(window, "JumpToLatestButton").IsVisible);

        window.ScrollMessagesWithMiddleButtonForTesting(100);
        await WaitForAsync(() => viewModel.IsFollowingLatest, TimeSpan.FromSeconds(1));
        Assert.False(AssertControl<Button>(window, "JumpToLatestButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task MainStatesRenderAtSupportedWindowSizes()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

        Assert.Equal("0.6", WitherChat.Core.AppVersion.Current);
        Assert.Equal(860, window.MinWidth);
        Assert.Equal(560, window.MinHeight);

        RenderAndAssert(window, 1100, 760, "main-ru-dark-1100x760.png");
        AssertControl<Button>(window, "ConnectButton");
        AssertControl<Button>(window, "ChannelSwitcherButton");
        AssertControl<Button>(window, "SettingsButton");
        var chatLogsButton = AssertControl<Button>(window, "ChatLogsButton");
        var chatLogsIcon = Assert.Single(chatLogsButton.GetLogicalDescendants()
            .OfType<global::Avalonia.Controls.Shapes.Path>());
        var chatLogsIconCenter = chatLogsIcon.TranslatePoint(
            new Point(chatLogsIcon.Bounds.Width / 2, chatLogsIcon.Bounds.Height / 2),
            chatLogsButton);
        Assert.NotNull(chatLogsIconCenter);
        Assert.Equal(15.5, chatLogsIconCenter.Value.X, precision: 1);
        Assert.Equal(17.5, chatLogsIconCenter.Value.Y, precision: 1);
        var moreToolsButton = AssertControl<Button>(window, "HeaderMoreToolsButton");
        var moreToolsFlyout = Assert.IsType<Flyout>(moreToolsButton.Flyout);
        viewModel.ReduceMotion = false;
        moreToolsFlyout.ShowAt(moreToolsButton);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var moreToolsContent = Assert.IsType<Border>(moreToolsFlyout.Content);
        Assert.Equal(2, moreToolsContent.Transitions?.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(160),
            Assert.Single(moreToolsContent.Transitions!.OfType<DoubleTransition>()).Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(160),
            Assert.Single(moreToolsContent.Transitions!.OfType<TransformOperationsTransition>()).Duration);
        Assert.Equal(new RelativePoint(1, 0, RelativeUnit.Relative),
            moreToolsContent.RenderTransformOrigin);
        Assert.Equal(new CornerRadius(16), moreToolsContent.CornerRadius);
        Assert.Equal(new Thickness(1), moreToolsContent.BorderThickness);
        Assert.NotNull(moreToolsContent.Background);
        var allMoreToolItems = moreToolsContent.GetLogicalDescendants().OfType<Button>().ToArray();
        Assert.Equal(8, allMoreToolItems.Length);
        var dockOnlyItems = allMoreToolItems.Where(item => AutomationProperties.GetAutomationId(item)
            is "ObsDockDonationMenuItem" or "ObsDockClipMenuItem").ToArray();
        Assert.Equal(2, dockOnlyItems.Length);
        Assert.All(dockOnlyItems, item => Assert.False(item.IsVisible));
        var moreToolItems = allMoreToolItems.Except(dockOnlyItems).ToArray();
        Assert.Equal(6, moreToolItems.Length);
        Assert.All(moreToolItems, item => Assert.NotNull(item.Command));
        Assert.All(moreToolItems, item => Assert.Equal(new CornerRadius(12), item.CornerRadius));
        Assert.All(moreToolItems, item =>
        {
            var iconBackground = Assert.Single(item.GetLogicalDescendants().OfType<Border>());
            Assert.Equal(28, iconBackground.Width);
            Assert.Equal(28, iconBackground.Height);
            Assert.Equal(HorizontalAlignment.Center, iconBackground.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, iconBackground.VerticalAlignment);
            var icon = Assert.Single(iconBackground.GetLogicalDescendants()
                .OfType<global::Avalonia.Controls.Shapes.Path>());
            Assert.Equal(16, icon.Width);
            Assert.Equal(16, icon.Height);
            Assert.Equal(HorizontalAlignment.Center, icon.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Center, icon.VerticalAlignment);
        });
        Assert.All(moreToolItems, item => Assert.Contains(
            item.GetLogicalDescendants().OfType<TextBlock>(),
            text => !string.IsNullOrWhiteSpace(text.Text)));
        var momentsItem = Assert.Single(moreToolItems, item => string.Equals(
            AutomationProperties.GetAutomationId(item), "MoreStreamMomentsItem", StringComparison.Ordinal));
        var momentsIcon = Assert.Single(momentsItem.GetLogicalDescendants()
            .OfType<global::Avalonia.Controls.Shapes.Path>());
        Assert.Equal(8, momentsIcon.Data!.Bounds.X + (momentsIcon.Data.Bounds.Width / 2), precision: 2);
        Assert.Equal(8, momentsIcon.Data.Bounds.Y + (momentsIcon.Data.Bounds.Height / 2), precision: 2);
        Assert.Equal(Stretch.None, momentsIcon.Stretch);
        moreToolsFlyout.Hide();

        moreToolsFlyout.ShowAt(moreToolsButton);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var eventsItem = Assert.Single(moreToolItems, item => string.Equals(
            AutomationProperties.GetAutomationId(item), "MoreStreamEventsItem", StringComparison.Ordinal));
        var popupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(eventsItem));
        ClickAtCenter(popupRoot, eventsItem);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.IsStreamEventsOpen);
        Assert.False(moreToolsFlyout.IsOpen);
        viewModel.CloseStreamEventsCommand.Execute(null);

        moreToolsFlyout.ShowAt(moreToolsButton);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var momentsPopupRoot = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(momentsItem));
        ClickAtCenter(momentsPopupRoot, momentsItem);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.IsMomentsPanelOpen);
        Assert.False(moreToolsFlyout.IsOpen);
        viewModel.CloseMomentsPanelCommand.Execute(null);

        viewModel.ReduceMotion = true;
        moreToolsFlyout.ShowAt(moreToolsButton);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Empty(moreToolsContent.Transitions!);
        Assert.Equal(1, moreToolsContent.Opacity);
        moreToolsFlyout.Hide();
        viewModel.ReduceMotion = false;
        AssertControl<Button>(window, "CompactModeButton");
        var headerPanel = window.FindControl<Border>("HeaderPanel")!;
        var headerStatusStrip = AssertControl<Border>(window, "HeaderStatusStrip");
        var headerToolsStrip = AssertControl<Border>(window, "HeaderToolsStrip");
        Assert.InRange(headerPanel.Bounds.Height, 58, 64);
        Assert.InRange(headerStatusStrip.Bounds.Height, 26, 32);
        Assert.InRange(headerToolsStrip.Bounds.Height, 36, 44);
        foreach (var iconButton in window.FindControl<StackPanel>("HeaderToolsPanel")!.Children
                     .OfType<Button>()
                     .Where(button => button.IsVisible && button.Classes.Contains("icon-button")))
        {
            Assert.Equal(32, iconButton.Bounds.Width, precision: 1);
            Assert.Equal(32, iconButton.Bounds.Height, precision: 1);
        }
        Assert.True(AssertControl<Border>(window, "PinnedMessageCard").IsVisible);
        Assert.True(window.FindControl<StackPanel>("HeaderToolsPanel")!.Children
            .OfType<Button>().Count(button => button.IsVisible) <= 7);

        // Visual baselines must capture settled tutorial states; animated timing is
        // covered separately by OnboardingLiquidFocusMovesBetweenRealPanels.
        viewModel.ReduceMotion = true;
        viewModel.StartOnboardingCommand.Execute(null);
        await Task.Delay(80);
        Assert.True(viewModel.IsOnboardingOpen);
        Assert.Equal(0, viewModel.OnboardingStep);
        AssertControl<Border>(window, "OnboardingCard");
        AssertControl<Button>(window, "OnboardingSkipButton");
        AssertControl<Button>(window, "OnboardingNextButton");
        var onboardingDragArea = AssertControl<Border>(window, "OnboardingWindowDragArea");
        Assert.Equal(42, onboardingDragArea.Bounds.Height, precision: 1);
        Assert.True(onboardingDragArea.IsEffectivelyVisible);
        RenderAndAssert(window, 1100, 760, "onboarding-welcome-ru-dark-1100x760.png");
        viewModel.Language = "en";
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        RenderAndAssert(window, 1100, 760, "onboarding-welcome-en-light-1100x760.png");
        viewModel.Language = "ru";
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

        for (var step = 1; step <= 6; step++)
        {
            viewModel.NextOnboardingCommand.Execute(null);
            await Task.Delay(130);
            if (step == 2)
            {
                Assert.True(viewModel.IsChannelEditorOpen);
                Assert.True(AssertControl<Border>(window, "ChannelEditorCard").IsVisible);
            }
            else
            {
                Assert.False(viewModel.IsChannelEditorOpen);
            }
            RenderAndAssert(
                window,
                1100,
                760,
                step == 6
                    ? "onboarding-logs-ru-dark-1100x760.png"
                    : $"onboarding-step-{step + 1}-ru-dark-1100x760.png");
        }
        Assert.Equal(6, viewModel.OnboardingStep);
        Assert.True(viewModel.IsLogViewerOpen);
        Assert.False(viewModel.IsSettingsOpen);
        Assert.True(window.FindControl<Border>("LogViewerCard")!.IsEffectivelyVisible);

        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(420);
        Assert.Equal(7, viewModel.OnboardingStep);
        Assert.True(viewModel.IsSettingsOpen);
        Assert.False(viewModel.IsLogViewerOpen);
        Assert.True(viewModel.IsOverlaySettingsSelected);
        Assert.True(window.FindControl<Border>("OverlaySettingsCard")!.IsEffectivelyVisible);
        RenderAndAssert(window, 1100, 760, "onboarding-overlay-ru-dark-1100x760.png");

        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(130);
        Assert.Equal(8, viewModel.OnboardingStep);
        Assert.True(viewModel.IsSettingsOpen);
        Assert.True(viewModel.IsProgramSettingsSelected);
        AssertControl<Button>(window, "OnboardingBackButton");
        RenderAndAssert(window, 1100, 760, "onboarding-settings-ru-dark-1100x760.png");

        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(80);
        Assert.Equal(9, viewModel.OnboardingStep);
        Assert.False(viewModel.IsSettingsOpen);
        RenderAndAssert(window, 860, 560, "onboarding-compact-ru-dark-860x560.png");

        for (var step = 10; step < viewModel.OnboardingTotalSteps; step++)
        {
            viewModel.NextOnboardingCommand.Execute(null);
            await Task.Delay(160);
            Assert.Equal(step, viewModel.OnboardingStep);
            Assert.True(viewModel.IsOverlaySettingsSelected);
            RenderAndAssert(window, 1100, 760, $"onboarding-obs-{step}-ru-dark-1100x760.png");
        }
        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(30);
        Assert.False(viewModel.IsOnboardingOpen);
        viewModel.ReduceMotion = false;
        Assert.False(viewModel.IsSettingsOpen);

        viewModel.OpenConnectPanelCommand.Execute(null);
        await Task.Delay(100);
        RenderAndAssert(window, 1100, 760, "connect-panel-ru-dark-1100x760.png");
        var channelInput = AssertControl<TextBox>(window, "ConnectPanelChannelInput");
        var manualTab = AssertControl<Button>(window, "ConnectPanelManualTab");
        var followedTab = AssertControl<Button>(window, "ConnectPanelFollowedTab");
        Assert.True(channelInput.IsEffectivelyVisible);
        viewModel.ShowFollowedChannelsTabCommand.Execute(null);
        await Task.Delay(30);
        Assert.True(viewModel.IsFollowedChannelsTabSelected);
        Assert.False(channelInput.IsEffectivelyVisible);
        RenderAndAssert(window, 1100, 760, "followed-channels-sign-in-ru-dark-1100x760.png");
        viewModel.ShowManualChannelTabCommand.Execute(null);
        await Task.Delay(30);
        Assert.True(viewModel.IsManualChannelTabSelected);
        Assert.True(channelInput.IsEffectivelyVisible);
        var connectPanelBack = AssertControl<Button>(window, "ConnectPanelBackButton");
        Assert.True(connectPanelBack.IsVisible);
        var connectPanelSignIn = AssertControl<Button>(window, "ConnectPanelSignIn");
        var twitchLogo = window.FindControl<Border>("ConnectPanelTwitchLogo")!;
        var watchIcon = window.FindControl<Border>("ConnectPanelWatchIcon")!;
        Assert.True(connectPanelSignIn.Bounds.Width >= 600);
        Assert.Equal(40, twitchLogo.Bounds.Width);
        Assert.Equal(40, twitchLogo.Bounds.Height);
        Assert.Equal(twitchLogo.Bounds.Size, watchIcon.Bounds.Size);
        var watchButton = AssertControl<Button>(window, "ConnectPanelWatchButton");
        Assert.False(watchButton.IsEnabled);
        viewModel.ConnectPanelChannel = "wither_101";
        await Task.Delay(420);
        Assert.True(viewModel.CanWatchChannel);
        Assert.True(watchButton.IsEnabled);
        var verifiedChannel = Assert.Single(viewModel.ChannelSearchResults);
        Assert.Equal("WitheR_101", verifiedChannel.DisplayName);
        Assert.Equal(42, verifiedChannel.ViewerCount);
        Assert.True(verifiedChannel.IsLive);
        viewModel.ConnectPanelChannel = "channel_that_does_not_exist";
        await Task.Delay(420);
        Assert.False(viewModel.CanWatchChannel);
        Assert.False(watchButton.IsEnabled);
        viewModel.ConnectPanelChannel = "wither_101";
        await Task.Delay(420);
        viewModel.CloseConnectPanelCommand.Execute(null);
        await Task.Delay(320);

        viewModel.Channel = "twitch";
        viewModel.PinnedMessageAuthor = "moderator";
        viewModel.PinnedMessageText = "Закреплённое сообщение";
        RenderAndAssert(window, 1100, 760, "pinned-ru-dark-1100x760.png");
        viewModel.Channel = string.Empty;
        viewModel.PinnedMessageAuthor = string.Empty;
        viewModel.PinnedMessageText = string.Empty;

        viewModel.IsCompactMode = true;
        window.MinWidth = 300;
        window.MinHeight = 400;
        RenderAndAssert(window, 360, 400, "compact-ru-dark-360x400.png");
        viewModel.IsCompactMode = false;
        window.MinWidth = 860;
        window.MinHeight = 560;
        await Task.Delay(320);

        var deletedMessage = new ChatMessage
        {
            Id = "deleted-visual",
            Channel = "witherchat",
            UserId = "42",
            UserLogin = "viewer",
            DisplayName = "Viewer",
            Text = "Это сообщение сохранено после удаления",
            Timestamp = DateTimeOffset.UtcNow,
            Parts = [ChatMessagePart.PlainText("Это сообщение сохранено после удаления")]
        };
        var deletedItem = new ChatMessageItemViewModel(
            deletedMessage,
            fixture.ImageCache,
            viewModel.Texts,
            owner: viewModel);
        deletedItem.MarkModerated(ChatMessageModerationState.Deleted);
        viewModel.Channel = deletedMessage.Channel;
        viewModel.Messages.AppendBatch([deletedItem], 100);
        viewModel.VisibleMessages.AppendBatch([deletedItem], 100);
        typeof(MainWindowViewModel)
            .GetMethod("NotifyVisibleMessageStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);
        RenderAndAssert(window, 1100, 760, "deleted-message-ru-dark-1100x760.png");
        var messagesList = window.FindControl<ListBox>("MessagesList");
        Assert.NotNull(messagesList);
        messagesList!.SelectedItem = deletedItem;
        Assert.Null(messagesList.SelectedItem);
        Assert.True(deletedItem.IsModerated);
        Assert.Equal("Сообщение удалено", deletedItem.ModerationStatus);
        viewModel.Messages.Clear();
        viewModel.VisibleMessages.Clear();
        viewModel.Channel = string.Empty;

        viewModel.IsAddingChannel = false;
        viewModel.SavedChannels.Clear();
        var ownChannelPreview = new ChannelSessionViewModel("wither_101")
        {
            DisplayName = "WitheR_101",
            IsPrimaryAccountChannel = true,
            IsActive = true
        };
        viewModel.SavedChannels.Add(ownChannelPreview);
        viewModel.SavedChannels.Add(new ChannelSessionViewModel("braeden")
        {
            DisplayName = "Braeden",
            IsLive = true,
            ViewerCount = 3
        });
        var accountSession = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "wither_101",
            TwitchApplication.RequiredScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        var applySession = typeof(MainWindowViewModel).GetMethod(
            "ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applySession);
        applySession!.Invoke(viewModel, [accountSession]);
        viewModel.AccountDisplayName = "WitheR_101";
        viewModel.SelectedSavedChannel = ownChannelPreview;
        viewModel.IsChannelEditorOpen = true;
        await Task.Delay(420);
        RenderAndAssert(window, 860, 560, "channels-account-ru-dark-860x560.png");
        var channelSelectButton = AssertControl<Button>(window, "SavedChannelSelectButton");
        Assert.True(
            channelSelectButton.Bounds.Width >= 230,
            $"Saved channel hit target width was {channelSelectButton.Bounds.Width:0.0}px.");
        Assert.False(ownChannelPreview.CanRemove);
        Assert.True(viewModel.CanAddSavedChannel);

        viewModel.StartAddChannelCommand.Execute(null);
        Assert.True(viewModel.IsConnectPanelOpen);
        viewModel.ConnectPanelChannel = "bra";
        await Task.Delay(420);
        Assert.Equal(6, viewModel.ChannelSearchResults.Count);
        Assert.Contains(viewModel.ChannelSearchResults, result =>
            string.Equals(result.Login, "bratishkinoff", StringComparison.Ordinal));
        Assert.Contains(viewModel.ChannelSearchResults, result =>
            string.Equals(result.Login, "braeden", StringComparison.Ordinal) &&
            result.ViewerCount == 314);
        RenderAndAssert(window, 1100, 760, "channel-search-large-ru-dark-1100x760.png");
        var searchResults = AssertControl<Border>(window, "ConnectPanelSearchResults");
        Assert.True(searchResults.IsVisible);
        var firstSearchResult = searchResults.GetLogicalDescendants().OfType<Button>().First();
        Assert.True(firstSearchResult.Bounds.Width >= 500);
        viewModel.CloseConnectPanelCommand.Execute(null);

        viewModel.IsChannelEditorOpen = false;
        viewModel.IsSettingsOpen = true;
        viewModel.SelectedSettingsSection = SettingsSection.Program;
        RenderAndAssert(window, 1100, 760, "settings-program-ru-dark-1100x760.png");
        AssertControl<ComboBox>(window, "LanguageComboBox");
        AssertControl<ComboBox>(window, "ThemeComboBox");
        AssertControl<ComboBox>(window, "FontComboBox");
        AssertControl<ComboBox>(window, "WindowControlsStyleComboBox");
        Assert.Equal(AssertControl<ComboBox>(window, "LanguageComboBox").Bounds.Width,
            AssertControl<ComboBox>(window, "FontComboBox").Bounds.Width, precision: 1);
        Assert.True(AssertControl<Button>(window, "ProgramSettingsNav").Bounds.Width >= 180);
        var chatSettingsNav = AssertControl<Button>(window, "ChatSettingsNav");
        Assert.True(chatSettingsNav.Bounds.Width >= 180);
        chatSettingsNav.Command!.Execute(chatSettingsNav.CommandParameter);
        Assert.True(viewModel.IsChatSettingsSelected);
        RenderAndAssert(window, 1100, 760, "settings-chat-ru-dark-1100x760.png");
        var chatFontSize = AssertControl<TextBox>(window, "ChatFontSizeTextBox");
        var messageLimit = AssertControl<TextBox>(window, "MessageLimitTextBox");
        var viewerRefresh = AssertControl<TextBox>(window, "ViewerRefreshTextBox");
        Assert.Equal(chatFontSize.Bounds.Size, messageLimit.Bounds.Size);
        Assert.Equal(messageLimit.Bounds.Size, viewerRefresh.Bounds.Size);
        Assert.Equal(chatFontSize.Bounds.X, messageLimit.Bounds.X, precision: 1);
        Assert.Equal(messageLimit.Bounds.X, viewerRefresh.Bounds.X, precision: 1);
        Assert.Equal(5, chatFontSize.MaxLength);
        Assert.Equal(5, messageLimit.MaxLength);
        Assert.Equal(3, viewerRefresh.MaxLength);
        Assert.Equal(
            ((Grid)chatFontSize.Parent!).Bounds.Width,
            ((Grid)messageLimit.Parent!).Bounds.Width,
            precision: 1);
        Assert.Equal(
            ((Grid)messageLimit.Parent!).Bounds.Width,
            ((Grid)viewerRefresh.Parent!).Bounds.Width,
            precision: 1);
        viewModel.ShowProgramSettingsCommand.Execute(null);
        Assert.Equal(viewModel.ChatLogDirectory, viewModel.ChatLogsFolderDisplay);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ChatLogsFolderDisplay));

        viewModel.SelectedSettingsSection = SettingsSection.Overlay;
        viewModel.OverlayAlign = "center";
        RenderAndAssert(window, 1100, 760, "settings-overlay-ru-dark-1100x760.png");
        AssertControl<ComboBox>(window, "MessageVisualThemeComboBox");
        var overlayPort = AssertControl<TextBox>(window, "OverlayPortTextBox");
        var overlayMessages = AssertControl<TextBox>(window, "OverlayMaxMessagesTextBox");
        var overlayFontSize = AssertControl<TextBox>(window, "OverlayFontSizeTextBox");
        var overlayFade = AssertControl<TextBox>(window, "OverlayFadeTextBox");
        var overlayOpacity = AssertControl<Slider>(window, "OverlayOpacitySlider");
        var overlayOpacityValue = AssertControl<TextBlock>(window, "OverlayOpacityValue");
        var overlayAlignment = AssertControl<ComboBox>(window, "OverlayAlignmentComboBox");
        Assert.All(
            new[] { overlayPort, overlayMessages, overlayFontSize, overlayFade },
            field =>
            {
                Assert.Equal(TextAlignment.Left, field.TextAlignment);
                Assert.Equal(VerticalAlignment.Center, field.VerticalContentAlignment);
            });
        Assert.Equal(overlayPort.Bounds.Width, overlayMessages.Bounds.Width, precision: 1);
        Assert.Equal(overlayMessages.Bounds.Width, overlayFontSize.Bounds.Width, precision: 1);
        Assert.True(overlayAlignment.Bounds.Width >= 140);
        Assert.Equal("По центру", viewModel.SelectedOverlayAlignmentOption?.Label);
        Assert.Equal(5, overlayPort.MaxLength);
        Assert.Equal(3, overlayMessages.MaxLength);
        Assert.Equal(5, overlayFontSize.MaxLength);
        Assert.Equal(3, overlayFade.MaxLength);
        Assert.Equal(0, overlayOpacity.Minimum);
        Assert.Equal(1, overlayOpacity.Maximum);
        Assert.Equal(0.05, overlayOpacity.TickFrequency);
        Assert.Equal("0%", overlayOpacityValue.Text);

        var oversizedNumber = new string('9', 64);
        viewModel.ChatFontSizeText = oversizedNumber;
        viewModel.MessageLimitText = oversizedNumber;
        viewModel.ViewerCountRefreshIntervalText = oversizedNumber;
        viewModel.OverlayPortText = oversizedNumber;
        viewModel.OverlayMaxMessagesText = oversizedNumber;
        viewModel.OverlayFontSizeText = oversizedNumber;
        viewModel.OverlayFadeOutSecondsText = oversizedNumber;
        viewModel.OverlayBackgroundOpacityText = oversizedNumber;
        Assert.Equal(28, viewModel.ChatFontSize);
        Assert.Equal(10_000, viewModel.MessageLimit);
        Assert.Equal(600, viewModel.ViewerCountRefreshIntervalSeconds);
        Assert.Equal(65535, viewModel.OverlayPort);
        Assert.Equal(100, viewModel.OverlayMaxMessages);
        Assert.Equal(72, viewModel.OverlayFontSize);
        Assert.Equal(600, viewModel.OverlayFadeOutSeconds);
        Assert.Equal(1, viewModel.OverlayBackgroundOpacity);
        Assert.Equal("100%", viewModel.OverlayBackgroundOpacityPercent);

        viewModel.SelectedSettingsSection = SettingsSection.Account;
        RenderAndAssert(window, 1100, 760, "settings-account-ru-dark-1100x760.png");
        Assert.True(AssertControl<Border>(window, "AccountSettingsAvatar").IsVisible);
        var accountLink = AssertControl<Button>(window, "AccountChannelLink");
        Assert.Equal("https://www.twitch.tv/wither_101",
            Assert.IsType<TextBlock>(accountLink.Content).Text);
        Assert.True(accountLink.IsVisible);
        var connectYouTube = AssertControl<Button>(window, "ConnectYouTubeButton");
        var youTubeAccountLabel = AssertControl<TextBlock>(window, "YouTubeAccountLabel");
        Assert.Null(window.FindControl<TextBox>("YouTubeClientIdTextBox"));
        Assert.True(connectYouTube.IsVisible);
        Assert.True(connectYouTube.IsEnabled);
        Assert.False(youTubeAccountLabel.IsVisible);

        viewModel.SelectedSettingsSection = SettingsSection.Donate;
        RenderAndAssert(window, 1100, 760, "settings-donate-branded-ru-dark-1100x760.png");
        var boostySupport = AssertControl<Button>(window, "BoostySupportButton");
        var donationAlertsSupport = AssertControl<Button>(window, "DonationAlertsSupportButton");
        var twitchSupport = AssertControl<Button>(window, "TwitchSupportButton");
        var youtubeSupport = AssertControl<Button>(window, "YouTubeSupportButton");
        var steamSupport = AssertControl<Button>(window, "SteamSupportButton");
        var telegramSupport = AssertControl<Button>(window, "TelegramSupportButton");
        var tronWallet = AssertControl<Button>(window, "TronWalletButton");
        var tonWallet = AssertControl<Button>(window, "TonWalletButton");
        var bscWallet = AssertControl<Button>(window, "BscWalletButton");
        Assert.All(
            new[]
            {
                boostySupport,
                donationAlertsSupport,
                twitchSupport,
                youtubeSupport,
                steamSupport,
                telegramSupport
            },
            button => Assert.True(button.Bounds.Width > 250));
        Assert.Equal(boostySupport.Bounds.Width, donationAlertsSupport.Bounds.Width, precision: 1);
        Assert.Equal(tronWallet.Bounds.Width, tonWallet.Bounds.Width, precision: 1);
        Assert.Equal(tonWallet.Bounds.Width, bscWallet.Bounds.Width, precision: 1);
        Assert.True(tronWallet.Bounds.Width > 550);
        Assert.Equal("TDU8U6fcK6qr1hhYCzZXgNYKbcpxH5oVn3", viewModel.UsdtTrc20Address);
        Assert.Equal("UQC7ap0mFUP8KfGjDUGvpgIcy8FqyKPTdI0zoj5PZMYawdXC", viewModel.UsdtTonAddress);
        Assert.Equal("0xfa5285ecdb951a990c47432548f3361e681ed940", viewModel.UsdtBscAddress);
        foreach (var assetName in new[]
                 {
                     "boosty.png",
                     "donationalerts.png",
                     "twitch.png",
                     "youtube.png",
                     "steam.png",
                     "telegram.png",
                     "tron.png",
                     "ton.png",
                     "bsc.png"
                 })
        {
            var assetPath = Path.Combine(
                FindRepositoryRoot(),
                "src",
                "WitherChat.Desktop",
                "Assets",
                "Brands",
                assetName);
            using var brandAsset = SKBitmap.Decode(File.ReadAllBytes(assetPath));
            Assert.NotNull(brandAsset);
            Assert.True(
                brandAsset!.Width >= 512 && brandAsset.Height >= 512,
                $"Donate brand asset {assetName} must be at least 512×512, but is {brandAsset.Width}×{brandAsset.Height}.");
        }

        viewModel.IsSettingsOpen = false;
        viewModel.IsLogViewerOpen = true;
        await Task.Delay(100);
        RenderAndAssert(window, 1100, 760, "chat-logs-empty-ru-dark-1100x760.png");
        var closeLogs = AssertControl<Button>(window, "CloseLogsButton");
        Assert.Equal(closeLogs.Bounds.Width, closeLogs.Bounds.Height, precision: 1);
        var openLogFolder = AssertControl<Button>(window, "OpenLogFolderButton");
        var logSidebar = AssertControl<Border>(window, "LogViewerSidebar");
        var logContent = AssertControl<Border>(window, "LogViewerContent");
        var logActions = AssertControl<StackPanel>(window, "LogViewerActions");
        var logSearch = AssertControl<TextBox>(window, "LogSearchInput");
        var logUserFilter = AssertControl<TextBox>(window, "LogUserFilterInput");
        var logRoleFilter = AssertControl<ComboBox>(window, "LogRoleFilter");
        Assert.True(openLogFolder.IsVisible);
        Assert.Equal(Orientation.Horizontal, logActions.Orientation);
        Assert.Equal(250, logSidebar.Bounds.Width, precision: 1);
        Assert.True(logContent.Bounds.Width > logSidebar.Bounds.Width);
        Assert.True(logSearch.Bounds.Width > logUserFilter.Bounds.Width);
        Assert.Equal(logSearch.Bounds.Height, logUserFilter.Bounds.Height, precision: 1);
        Assert.Equal(logUserFilter.Bounds.Height, logRoleFilter.Bounds.Height, precision: 1);
        viewModel.SelectedLogFile = new ChatLogFileViewModel(
            "C:\\logs\\recrent\\2026-07-29\\chat.jsonl",
            "recrent",
            new DateTime(2026, 7, 29),
            1024);
        viewModel.IsDeleteLogConfirmationOpen = true;
        await Task.Delay(40);
        Assert.Contains(
            "2026",
            AssertControl<TextBlock>(window, "DeleteLogStreamDate").Text);
        viewModel.IsDeleteLogConfirmationOpen = false;
        viewModel.IsLogViewerOpen = false;

        viewModel.IsModerationPanelOpen = true;
        await Task.Delay(240);
        viewModel.ModerationPanelSection = 1;
        viewModel.Language = "en";
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        RenderAndAssert(window, 1100, 760, "moderation-en-light-1100x760.png");
        var closeModeration = AssertControl<Button>(window, "CloseModerationButton");
        Assert.Equal(closeModeration.Bounds.Width, closeModeration.Bounds.Height, precision: 1);
    }

    [AvaloniaFact]
    public void ServicePanelsExposeScrollingWhileChatKeepsCleanLayout()
    {
        var window = new MainWindow();
        foreach (var name in new[] { "LogChannelList", "LogFileList", "LogEntriesList",
                     "AutoModMessagesList", "BannedUsersList", "UnbanRequestsList" })
        {
            var list = window.GetLogicalDescendants().OfType<ListBox>().Single(control =>
                control.Name == name || AutomationProperties.GetAutomationId(control) == name);
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                ScrollViewer.GetVerticalScrollBarVisibility(list));
        }
        Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            window.FindControl<ScrollViewer>("SettingsContentScrollViewer")!.VerticalScrollBarVisibility);
        Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            ScrollViewer.GetVerticalScrollBarVisibility(window.FindControl<ListBox>("MessagesList")!));
        window.Content = null;
        window.Close();
    }

    [AvaloniaFact]
    public void LightThemeStatusAndSecondaryTextColorsMeetReadableContrast()
    {
        var application = Application.Current!;
        var white = Color.Parse("#FFFFFF");
        foreach (var resourceKey in new[]
                 {
                     "MutedTextBrush",
                     "DangerBrush",
                     "WarningBrush",
                     "DonationAccentBrush",
                     "DonationAccentSoftBrush"
                 })
        {
            Assert.True(application.TryGetResource(resourceKey, ThemeVariant.Light, out var resource));
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(resource);
            Assert.True(
                ContrastRatio(brush.Color, white) >= 4.5,
                $"{resourceKey} has insufficient light-theme contrast: {brush.Color}.");
        }

        static double ContrastRatio(Color first, Color second)
        {
            var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
            var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
            return (lighter + 0.05) / (darker + 0.05);
        }

        static double RelativeLuminance(Color color)
        {
            static double Linear(byte component)
            {
                var value = component / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
    }

    [AvaloniaFact]
    public async Task UserInputFieldsExposeLocalizedAutomationNames()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.IsSettingsOpen = true;
        window.Show();
        window.Width = 860;
        window.Height = 560;

        var cases = new (SettingsSection Section, string CardName, int FieldCount)[]
        {
            (SettingsSection.Program, "ProgramSettingsCard", 5),
            (SettingsSection.Chat, "ChatSettingsCard", 3),
            (SettingsSection.ChatLogs, "ChatLogsSettingsCard", 2),
            (SettingsSection.Overlay, "OverlaySettingsCard", 8),
            (SettingsSection.Advanced, "AdvancedSettingsCard", 2)
        };

        foreach (var item in cases)
        {
            viewModel.SelectedSettingsSection = item.Section;
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(860, 560));
            window.Arrange(new Rect(0, 0, 860, 560));

            var card = window.FindControl<Border>(item.CardName);
            Assert.NotNull(card);
            var fields = card!.GetLogicalDescendants()
                .OfType<Control>()
                .Where(control => control is TextBox or ComboBox or Slider)
                .ToArray();
            Assert.Equal(item.FieldCount, fields.Length);
            Assert.All(
                fields,
                field => Assert.False(
                    string.IsNullOrWhiteSpace(AutomationProperties.GetName(field)),
                    $"{item.CardName} contains an unnamed {field.GetType().Name}."));
        }

        foreach (var automationId in new[]
                 {
                     "ComposerInput",
                     "MessageSearchInput",
                     "UserFilterInput",
                     "ChannelInput",
                     "LogSearchInput",
                     "LogUserFilterInput",
                     "LogRoleFilter",
                     "ProtectionSlowModeToggle",
                     "ProtectionSlowSecondsInput",
                     "ProtectionSubscriberModeToggle",
                     "ProtectionFollowerModeToggle",
                     "ProtectionFollowerMinutesInput",
                     "ProtectionPauseDisplayToggle",
                     "ProtectionSuppressObsToggle",
                     "MomentNoteInput",
                     "ModerationUserLoginInput",
                     "ModerationReasonInput",
                     "ConnectPanelChannelInput",
                     "FollowedChannelsSearchInput"
                 })
        {
            var field = AssertControl<Control>(window, automationId);
            Assert.False(
                string.IsNullOrWhiteSpace(AutomationProperties.GetName(field)),
                $"{automationId} has no localized automation name.");
        }
    }

    [AvaloniaFact]
    public async Task OneClickTwitchClipCreationCoalescesRapidClicksAndExposesResultActions()
    {
        var handler = new ControlledTwitchClipHandler();
        await using var fixture = new WindowFixture(apiHandler: handler);
        var viewModel = fixture.ViewModel;
        var window = fixture.Window;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var session = new TwitchAuthSession(
            "token",
            string.Empty,
            TwitchApplication.ClientId,
            "account-id",
            "clip_creator",
            TwitchApplication.ChatScopes,
            DateTimeOffset.UtcNow.AddHours(1),
            DateTimeOffset.UtcNow);
        typeof(MainWindowViewModel)
            .GetMethod("ApplyAuthenticatedSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [session]);
        viewModel.Channel = "live_channel";
        Uri? openedUri = null;
        string? copiedText = null;
        viewModel.OpenUriRequested += (_, eventArgs) => openedUri = eventArgs.Value;
        viewModel.CopyTextRequested += (_, eventArgs) => copiedText = eventArgs.Value;

        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.IsStreamStatusKnown = true;
        viewModel.IsStreamLive = true;
        var clipButton = AssertControl<Button>(window, "CreateTwitchClipButton");
        Assert.True(clipButton.IsEffectivelyVisible);
        Assert.True(clipButton.IsEnabled);
        Assert.Same(viewModel.CreateTwitchClipCommand, clipButton.Command);
        viewModel.CreateTwitchClipCommand.Execute(null);
        await WaitForAsync(() => handler.ClipRequestCount == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.ClipRequestCount);
        Assert.True(viewModel.IsCreatingTwitchClip);
        Assert.True(AssertControl<Border>(window, "TwitchClipNotification").IsVisible);

        for (var index = 0; index < 200; index++)
        {
            viewModel.CreateTwitchClipCommand.Execute(null);
        }
        await Task.Delay(60);
        Assert.Equal(1, handler.ClipRequestCount);

        handler.Release();
        await WaitForAsync(
            () => viewModel.HasCreatedTwitchClip && !viewModel.IsCreatingTwitchClip,
            TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.ClipRequestCount);
        Assert.Equal("https://clips.twitch.tv/HeadlessClipSlug", viewModel.CreatedTwitchClipShareUri?.AbsoluteUri);
        var notification = AssertControl<Border>(window, "TwitchClipNotification");
        Assert.True(notification.IsVisible);
        Assert.InRange(notification.Bounds.Width, 320, 380);
        RenderAndAssert(window, 1100, 760, "twitch-clip-created-ru-dark-1100x760.png");
        RenderAndAssert(window, 860, 560, "twitch-clip-created-ru-dark-860x560.png");
        var notificationTopLeft = notification.TranslatePoint(default, window);
        Assert.NotNull(notificationTopLeft);
        Assert.True(notificationTopLeft.Value.X >= 0);
        Assert.True(notificationTopLeft.Value.X + notification.Bounds.Width <= 860);
        var tools = AssertControl<Border>(window, "HeaderToolsStrip");
        var toolsTopLeft = tools.TranslatePoint(default, window);
        Assert.NotNull(toolsTopLeft);
        Assert.True(toolsTopLeft.Value.X + tools.Bounds.Width <= 860);

        var openButton = AssertControl<Button>(window, "OpenCreatedTwitchClipButton");
        var copyButton = AssertControl<Button>(window, "CopyCreatedTwitchClipButton");
        Assert.Same(viewModel.OpenCreatedTwitchClipCommand, openButton.Command);
        Assert.Same(viewModel.CopyCreatedTwitchClipLinkCommand, copyButton.Command);
        viewModel.OpenCreatedTwitchClipCommand.Execute(null);
        viewModel.CopyCreatedTwitchClipLinkCommand.Execute(null);
        Assert.Equal("https://www.twitch.tv/live_channel/clip/HeadlessClipSlug", openedUri?.AbsoluteUri);
        Assert.Equal("https://clips.twitch.tv/HeadlessClipSlug", copiedText);
    }

    [AvaloniaFact]
    public async Task ChannelEditorSlidesWithoutFlashingOverHeader()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = false;

        var card = AssertControl<Border>(window, "ChannelEditorCard");
        viewModel.IsChannelEditorOpen = true;
        Assert.True(card.IsVisible);
        Assert.Equal(0, card.Opacity);
        var header = window.FindControl<Border>("HeaderPanel")!;
        var headerBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), window);
        Assert.NotNull(headerBottom);
        Assert.InRange(card.Margin.Top, headerBottom.Value.Y + 7, headerBottom.Value.Y + 9);

        await WaitForAsync(() => card.Opacity > 0.99 && card.RenderTransform?.Value.IsIdentity == true,
            TimeSpan.FromSeconds(2));
        Assert.True(card.Opacity > 0.99);
        Assert.True(card.RenderTransform?.Value.IsIdentity == true);

        viewModel.IsChannelEditorOpen = false;
        Assert.True(card.IsVisible);
        Assert.False(card.IsHitTestVisible);
        Assert.Contains(card.Transitions ?? [], transition =>
            transition is global::Avalonia.Animation.DoubleTransition opacity && opacity.Duration > TimeSpan.Zero);
        await WaitForAsync(() => !card.IsVisible, TimeSpan.FromSeconds(2));
        Assert.False(card.IsVisible);
    }

    [AvaloniaFact]
    public async Task OnboardingLiquidFocusMovesBetweenRealPanels()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = false;

        var card = AssertControl<Border>(window, "OnboardingCard");
        var animatedContent = window.FindControl<StackPanel>("OnboardingAnimatedContent");
        Assert.NotNull(animatedContent);
        var focus = window.FindControl<Border>("OnboardingTargetHighlight")!;
        var focusCanvas = window.FindControl<Canvas>("OnboardingFocusCanvas")!;
        var dimmingMask = window.FindControl<global::Avalonia.Controls.Shapes.Path>(
            "OnboardingDimmingMask")!;
        var header = window.FindControl<Border>("HeaderPanel")!;

        viewModel.StartOnboardingCommand.Execute(null);
        await Task.Delay(720);
        var welcomeTop = card.Margin.Top;
        Assert.True(viewModel.IsOnboardingOpen);
        Assert.True(card.Opacity > 0.99);

        viewModel.NextOnboardingCommand.Execute(null);
        Assert.NotEmpty(card.Transitions ?? []);
        await Task.Delay(70);
        Assert.NotEmpty(animatedContent.Transitions ?? []);
        Assert.True(animatedContent.Opacity < 1);
        Assert.Contains(
            card.Transitions ?? [],
            transition => transition is global::Avalonia.Animation.ThicknessTransition travelTransition &&
                          travelTransition.Duration >= TimeSpan.FromMilliseconds(1000));
        await Task.Delay(140);
        Assert.Empty(dimmingMask.Transitions ?? []);
        Assert.Empty(focus.Transitions ?? []);
        Assert.Equal(1, focusCanvas.Opacity);
        await Task.Delay(1320);
        Assert.True(card.Margin.Top > welcomeTop + 40);
        Assert.True(card.Opacity > 0.99);
        Assert.True(animatedContent.Opacity > 0.99);
        Assert.True(animatedContent.RenderTransform?.Value.IsIdentity == true);
        var headerOrigin = header.TranslatePoint(new Point(), window);
        Assert.NotNull(headerOrigin);
        Assert.InRange(Canvas.GetLeft(focus), headerOrigin!.Value.X - 6, headerOrigin.Value.X - 4);
        Assert.InRange(focus.Width, header.Bounds.Width + 9, header.Bounds.Width + 11);
        Assert.NotNull(dimmingMask.Data);

        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(1420);
        Assert.True(viewModel.IsChannelEditorOpen);
        var channelCard = AssertControl<Border>(window, "ChannelEditorCard");
        Assert.True(channelCard.IsVisible);
        Assert.InRange(focus.Width, channelCard.Bounds.Width + 9, channelCard.Bounds.Width + 11);

        viewModel.PreviousOnboardingCommand.Execute(null);
        await Task.Delay(1420);
        Assert.False(viewModel.IsChannelEditorOpen);

        for (var step = 2; step < viewModel.OnboardingTotalSteps; step++)
        {
            viewModel.NextOnboardingCommand.Execute(null);
            await Task.Delay(1420);
            await WaitForOnboardingFocusSettledAsync(window, TestContext.Current.CancellationToken);
            Assert.Equal(step, viewModel.OnboardingStep);
            Assert.True(card.Opacity > 0.99);
            Assert.True(focus.Width > 20);
            Assert.Equal(step is 7 or 8 or 10 or 11, viewModel.IsSettingsOpen);
            Assert.Equal(step == 6, viewModel.IsLogViewerOpen);
            if (step == 7)
            {
                Assert.True(viewModel.IsOverlaySettingsSelected);
                var settingsViewport = window.FindControl<ScrollViewer>("SettingsContentScrollViewer");
                Assert.NotNull(settingsViewport);
                var viewportOrigin = settingsViewport!.TranslatePoint(new Point(), window);
                Assert.NotNull(viewportOrigin);
                var target = window.FindControl<Button>("CopyOverlayUrlButton")!;
                var targetOrigin = target.TranslatePoint(new Point(), window)!.Value;
                Assert.InRange(Canvas.GetTop(focus), Math.Max(viewportOrigin!.Value.Y, targetOrigin.Y - 5) - 0.5,
                    Math.Max(viewportOrigin.Value.Y, targetOrigin.Y - 5) + 0.5);
                Assert.True(Canvas.GetTop(focus) + focus.Height <= viewportOrigin.Value.Y + settingsViewport.Viewport.Height + 0.5);
                Assert.InRange(focus.Width, target.Bounds.Width + 9, target.Bounds.Width + 11);
            }
            if (step == 8)
            {
                Assert.True(viewModel.IsProgramSettingsSelected);
            }
            if (step == 4)
            {
                Assert.True(viewModel.ShowOnboardingComposerPreview);
                var composerPreview = window.FindControl<Border>("OnboardingComposerPreview");
                Assert.NotNull(composerPreview);
                var previewOrigin = composerPreview.TranslatePoint(new Point(), window);
                Assert.NotNull(previewOrigin);
                Assert.InRange(Canvas.GetLeft(focus), previewOrigin!.Value.X - 0.5, previewOrigin.Value.X + 0.5);
                Assert.InRange(Canvas.GetTop(focus), previewOrigin.Value.Y - 0.5, previewOrigin.Value.Y + 0.5);
                Assert.InRange(focus.Width, composerPreview.Bounds.Width - 0.5, composerPreview.Bounds.Width + 0.5);
                Assert.InRange(focus.Height, composerPreview.Bounds.Height - 0.5, composerPreview.Bounds.Height + 0.5);
            }
            if (step == 5)
            {
                Assert.True(focus.Width < 400);
            }
        }

        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(320);
        Assert.False(viewModel.IsOnboardingOpen);
    }

    [AvaloniaFact]
    public async Task ContextHelpButtonsOpenGuidesWithoutClosingTheirPanels()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = true;
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));

        async Task VerifyPanelGuideAsync(
            Action<bool> setPanelOpen,
            Func<bool> panelIsOpen,
            string helpButtonId,
            TutorialTopic expectedTopic)
        {
            setPanelOpen(true);
            await Task.Delay(40);
            window.Measure(new Size(1100, 760));
            window.Arrange(new Rect(0, 0, 1100, 760));
            var helpButton = AssertControl<Button>(window, helpButtonId);
            Assert.True(helpButton.IsEffectivelyVisible);
            Assert.Equal(28, helpButton.Bounds.Width, precision: 1);
            ClickAtCenter(window, helpButton);
            await Task.Delay(50);

            Assert.True(viewModel.IsOnboardingOpen);
            Assert.True(viewModel.IsContextTutorial);
            Assert.Equal(expectedTopic, viewModel.ActiveTutorialTopic);
            Assert.Equal(0, viewModel.OnboardingStep);
            Assert.True(panelIsOpen());
            Assert.True(AssertControl<Border>(window, "OnboardingOverlay").IsEffectivelyVisible);
            Assert.False(string.IsNullOrWhiteSpace(viewModel.OnboardingTitle));
            var target = window.FindControl<Border>("OnboardingTargetHighlight");
            var card = AssertControl<Border>(window, "OnboardingCard");
            Assert.NotNull(target);
            Assert.True(target.Width > 0 && target.Height > 0);
            Assert.True(card.Bounds.Width > 0 && card.Bounds.Height > 0);

            viewModel.SkipOnboardingCommand.Execute(null);
            await Task.Delay(30);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(viewModel.IsOnboardingOpen);
            Assert.True(panelIsOpen());
            Assert.True(helpButton.IsFocused);
            setPanelOpen(false);
            await Task.Delay(20);
        }

        await VerifyPanelGuideAsync(
            value => viewModel.IsChannelEditorOpen = value,
            () => viewModel.IsChannelEditorOpen,
            "ChannelHelpButton",
            TutorialTopic.Channels);
        await VerifyPanelGuideAsync(
            value => viewModel.IsLogViewerOpen = value,
            () => viewModel.IsLogViewerOpen,
            "ChatLogsHelpButton",
            TutorialTopic.Logs);
        await VerifyPanelGuideAsync(
            value => viewModel.IsModerationPanelOpen = value,
            () => viewModel.IsModerationPanelOpen,
            "ModerationHelpButton",
            TutorialTopic.Moderation);
        await VerifyPanelGuideAsync(
            value => viewModel.IsConnectPanelOpen = value,
            () => viewModel.IsConnectPanelOpen,
            "ConnectHelpButton",
            TutorialTopic.Connect);

        viewModel.SelectedSettingsSection = SettingsSection.Donate;
        viewModel.IsSettingsOpen = true;
        await Task.Delay(40);
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        ClickAtCenter(window, AssertControl<Button>(window, "SettingsHelpButton"));
        await Task.Delay(40);
        Assert.Equal(TutorialTopic.Settings, viewModel.ActiveTutorialTopic);
        viewModel.NextOnboardingCommand.Execute(null);
        Assert.Equal(SettingsSection.Program, viewModel.SelectedSettingsSection);
        viewModel.SkipOnboardingCommand.Execute(null);
        Assert.True(viewModel.IsSettingsOpen);
        Assert.Equal(SettingsSection.Donate, viewModel.SelectedSettingsSection);
    }

    [AvaloniaFact]
    public async Task ContextGuideNavigationFinishesWithoutCompletingFirstRunTour()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = true;
        viewModel.IsLogViewerOpen = true;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        viewModel.StartContextTutorialCommand.Execute("Logs");
        await Task.Delay(40);

        Assert.Equal(4, viewModel.OnboardingTotalSteps);
        Assert.Equal("1 / 4", viewModel.OnboardingProgressText);
        Assert.False(viewModel.CanGoBackInOnboarding);
        var russianTitle = viewModel.OnboardingTitle;
        viewModel.Language = "en";
        Assert.NotEqual(russianTitle, viewModel.OnboardingTitle);
        Assert.Equal("Archive by channel and day", viewModel.OnboardingTitle);
        viewModel.Language = "ru";
        var next = AssertControl<Button>(window, "OnboardingNextButton");
        ClickAtCenter(window, next);
        Assert.Equal(1, viewModel.OnboardingStep);
        var back = AssertControl<Button>(window, "OnboardingBackButton");
        Assert.True(back.IsEffectivelyVisible);
        ClickAtCenter(window, back);
        Assert.Equal(0, viewModel.OnboardingStep);
        for (var step = 1; step < 4; step++)
        {
            ClickAtCenter(window, next);
            Assert.Equal(step, viewModel.OnboardingStep);
        }
        Assert.Equal(viewModel.Texts.ContextTutorialFinish, viewModel.OnboardingNextLabel);
        ClickAtCenter(window, next);

        Assert.False(viewModel.IsOnboardingOpen);
        Assert.True(viewModel.IsLogViewerOpen);
        viewModel.StartInitialOnboarding();
        Assert.True(viewModel.IsOnboardingOpen);
        Assert.Equal(TutorialTopic.QuickStart, viewModel.ActiveTutorialTopic);
        Assert.Equal(TutorialCatalog.QuickStartStepCount, viewModel.OnboardingTotalSteps);
        viewModel.SkipOnboardingCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task ChatLogsGuideFocusMovesContinuouslyBetweenSteps()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = false;
        viewModel.IsLogViewerOpen = true;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.StartContextTutorialCommand.Execute("Logs");
        await Task.Delay(420);

        var focus = window.FindControl<Border>("OnboardingTargetHighlight");
        Assert.NotNull(focus);
        static Rect FocusRect(Border target) => new(
            Canvas.GetLeft(target),
            Canvas.GetTop(target),
            target.Width,
            target.Height);
        static double Distance(Rect left, Rect right) =>
            Math.Abs(left.X - right.X) +
            Math.Abs(left.Y - right.Y) +
            Math.Abs(left.Width - right.Width) +
            Math.Abs(left.Height - right.Height);

        var start = FocusRect(focus!);
        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(240);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var moving = FocusRect(focus);
        await Task.Delay(1200);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var settled = FocusRect(focus);

        Assert.True(Distance(start, moving) > 2, "The focus did not begin moving to the next logs target.");
        Assert.True(Distance(moving, settled) > 2, "The focus snapped to the next logs target instead of animating.");
        Assert.Equal(1, viewModel.OnboardingStep);

        viewModel.PreviousOnboardingCommand.Execute(null);
        await Task.Delay(240);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var movingBack = FocusRect(focus);
        await Task.Delay(1200);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var returned = FocusRect(focus);

        Assert.True(Distance(settled, movingBack) > 2, "The focus did not begin moving to the previous logs target.");
        Assert.True(Distance(movingBack, returned) > 2, "The focus snapped to the previous logs target instead of animating.");
        Assert.Equal(0, viewModel.OnboardingStep);
    }

    [AvaloniaFact]
    public async Task ContextGuidesSelectAndRestoreModerationAndSettingsSections()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = true;
        window.Show();
        window.Width = 1100;
        window.Height = 760;

        viewModel.IsModerationPanelOpen = true;
        viewModel.ModerationPanelSection = 1;
        viewModel.ModerationPlatform = 0;
        viewModel.StartContextTutorialCommand.Execute("Moderation");
        viewModel.NextOnboardingCommand.Execute(null);
        Assert.Equal(0, viewModel.ModerationPlatform);
        viewModel.NextOnboardingCommand.Execute(null);
        Assert.Equal(0, viewModel.ModerationPanelSection);
        viewModel.NextOnboardingCommand.Execute(null);
        Assert.Equal(1, viewModel.ModerationPanelSection);
        viewModel.NextOnboardingCommand.Execute(null);
        Assert.Equal(1, viewModel.ModerationPlatform);
        viewModel.SkipOnboardingCommand.Execute(null);
        Assert.Equal(1, viewModel.ModerationPanelSection);
        Assert.Equal(0, viewModel.ModerationPlatform);
        Assert.True(viewModel.IsModerationPanelOpen);

        viewModel.IsModerationPanelOpen = false;
        viewModel.IsSettingsOpen = true;
        viewModel.SelectedSettingsSection = SettingsSection.Donate;
        viewModel.StartContextTutorialCommand.Execute("Settings");
        var expectedSections = new[]
        {
            SettingsSection.Program,
            SettingsSection.Chat,
            SettingsSection.ChatLogs,
            SettingsSection.Overlay,
            SettingsSection.Account,
            SettingsSection.Donate,
            SettingsSection.Advanced
        };
        foreach (var expected in expectedSections)
        {
            viewModel.NextOnboardingCommand.Execute(null);
            window.Measure(new Size(1100, 760));
            window.Arrange(new Rect(0, 0, 1100, 760));
            await Task.Delay(140);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(expected, viewModel.SelectedSettingsSection);
            var target = window.FindControl<Border>("OnboardingTargetHighlight");
            Assert.NotNull(target);
            Assert.True(
                target.Width > 0 && target.Height > 0,
                $"Tutorial target is empty for settings section {expected} at step {viewModel.OnboardingStep}.");
        }
        viewModel.SkipOnboardingCommand.Execute(null);
        Assert.Equal(SettingsSection.Donate, viewModel.SelectedSettingsSection);
        Assert.True(viewModel.IsSettingsOpen);
    }

    [AvaloniaFact]
    public async Task DonationHelpOpensLocalGuideAndEscapeClosesOnlyTheGuide()
    {
        await using var fixture = new WindowFixture();
        fixture.ViewModel.ReduceMotion = true;
        ApplyDonationAlertsSessionForTesting(fixture.ViewModel);
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark,
            Width = 520,
            Height = 620
        };

        try
        {
            window.Show();
            window.Measure(new Size(520, 620));
            window.Arrange(new Rect(0, 0, 520, 620));
            ClickAtCenter(window, AssertControl<Button>(window, "DonationHelpButton"));
            await Task.Delay(60);
            window.Measure(new Size(520, 620));
            window.Arrange(new Rect(0, 0, 520, 620));

            Assert.Equal(TutorialTopic.Donations, fixture.ViewModel.ActiveTutorialTopic);
            Assert.True(fixture.ViewModel.ShowDonationTutorial);
            Assert.False(fixture.ViewModel.ShowMainWindowTutorial);
            Assert.Equal(4, fixture.ViewModel.OnboardingTotalSteps);
            var overlay = AssertControl<Grid>(window, "DonationTutorialOverlay");
            var card = AssertControl<Border>(window, "DonationTutorialCard");
            Assert.True(overlay.IsEffectivelyVisible);
            Assert.InRange(card.Bounds.Width, 378, 382);
            Assert.True(card.Bounds.Height > 0);
            Assert.True(card.Bounds.Height < window.Bounds.Height - 20);
            var tutorialNext = AssertControl<Button>(window, "DonationTutorialNextButton");
            Assert.True(tutorialNext.IsFocused);

            var helpButton = AssertControl<Button>(window, "DonationHelpButton");
            helpButton.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(0, fixture.ViewModel.OnboardingStep);
            Assert.True(tutorialNext.IsFocused);

            fixture.ViewModel.NextOnboardingCommand.Execute(null);
            Assert.Equal(1, fixture.ViewModel.OnboardingStep);
            fixture.ViewModel.NextOnboardingCommand.Execute(null);
            fixture.ViewModel.NextOnboardingCommand.Execute(null);
            Assert.Equal(3, fixture.ViewModel.OnboardingStep);
            fixture.ViewModel.CurrentDonation = new DonationAlert(
                "tutorial-current", "Viewer", "Tutorial donation", 500, "RUB", DateTimeOffset.UtcNow);
            window.Measure(new Size(520, 620));
            window.Arrange(new Rect(0, 0, 520, 620));
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var focus = window.FindControl<Border>("DonationTutorialTargetHighlight");
            var actionBar = window.FindControl<Border>("CurrentDonationActionBar");
            Assert.NotNull(focus);
            Assert.NotNull(actionBar);
            Assert.True(focus.Width > 0 && focus.Height > 0);
            var actionOrigin = actionBar.TranslatePoint(new Point(), window);
            Assert.NotNull(actionOrigin);
            Assert.InRange(Canvas.GetTop(focus), actionOrigin.Value.Y - 6, actionOrigin.Value.Y + 1);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Task.Delay(30);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(fixture.ViewModel.IsOnboardingOpen);
            Assert.True(window.IsVisible);
            Assert.True(helpButton.IsFocused);

            ClickAtCenter(window, AssertControl<Button>(window, "DonationHelpButton"));
            Assert.Equal(0, fixture.ViewModel.OnboardingStep);
        }
        finally
        {
            fixture.ViewModel.SkipOnboardingCommand.Execute(null);
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task DisconnectedDonationGuideExplainsConnectionWithoutHiddenHistoryTargets()
    {
        await using var fixture = new WindowFixture();
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark,
            Width = 460,
            Height = 520
        };

        try
        {
            window.Show();
            window.Measure(new Size(460, 520));
            window.Arrange(new Rect(0, 0, 460, 520));
            ClickAtCenter(window, AssertControl<Button>(window, "DonationHelpButton"));
            await Task.Delay(50);

            Assert.Equal(TutorialTopic.DonationsSetup, fixture.ViewModel.ActiveTutorialTopic);
            Assert.Equal(2, fixture.ViewModel.OnboardingTotalSteps);
            Assert.Contains("DonationAlerts", fixture.ViewModel.OnboardingTitle, StringComparison.Ordinal);
            var focus = window.FindControl<Border>("DonationTutorialTargetHighlight");
            Assert.NotNull(focus);
            Assert.True(focus.Width > 0 && focus.Height > 0);
            var connect = AssertControl<Button>(window, "DonationWindowConnectButton");
            Assert.True(connect.IsEffectivelyVisible);
        }
        finally
        {
            fixture.ViewModel.SkipOnboardingCommand.Execute(null);
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task ContextGuideSwitchesAtomicallyBetweenMainAndDonationWindows()
    {
        await using var fixture = new WindowFixture();
        fixture.ViewModel.ReduceMotion = true;
        var main = fixture.Window;
        var donations = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            Width = 520,
            Height = 620
        };

        try
        {
            main.Show();
            main.Width = 1100;
            main.Height = 760;
            donations.Show();
            fixture.ViewModel.IsLogViewerOpen = true;
            await Task.Delay(40);
            ClickAtCenter(main, AssertControl<Button>(main, "ChatLogsHelpButton"));
            await Task.Delay(40);
            Assert.True(AssertControl<Border>(main, "OnboardingOverlay").IsEffectivelyVisible);
            Assert.False(AssertControl<Grid>(donations, "DonationTutorialOverlay").IsEffectivelyVisible);

            ClickAtCenter(donations, AssertControl<Button>(donations, "DonationHelpButton"));
            await Task.Delay(50);
            Assert.False(AssertControl<Border>(main, "OnboardingOverlay").IsEffectivelyVisible);
            Assert.True(AssertControl<Grid>(donations, "DonationTutorialOverlay").IsEffectivelyVisible);

            ClickAtCenter(main, AssertControl<Button>(main, "ChatLogsHelpButton"));
            await Task.Delay(50);
            Assert.True(AssertControl<Border>(main, "OnboardingOverlay").IsEffectivelyVisible);
            Assert.False(AssertControl<Grid>(donations, "DonationTutorialOverlay").IsEffectivelyVisible);
            Assert.Equal(TutorialTopic.Logs, fixture.ViewModel.ActiveTutorialTopic);
        }
        finally
        {
            fixture.ViewModel.SkipOnboardingCommand.Execute(null);
            donations.DataContext = null;
            donations.CloseForApplicationExit();
        }
    }

    [Fact]
    public void ContextTutorialCatalogIsCompleteInRussianAndEnglish()
    {
        var texts = new UiText();
        foreach (var language in new[] { "ru", "en" })
        {
            texts.SetLanguage(language);
            foreach (var (topic, count) in TutorialCatalog.ContextGuides)
            {
                for (var step = 0; step < count; step++)
                {
                    Assert.False(string.IsNullOrWhiteSpace(texts.ContextTutorialTitle(topic, step)));
                    Assert.False(string.IsNullOrWhiteSpace(texts.ContextTutorialDescription(topic, step)));
                    Assert.False(string.IsNullOrWhiteSpace(texts.ContextTutorialHint(topic, step)));
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task ContextTutorialsRenderAtSupportedWindowSizes()
    {
        await using var fixture = new WindowFixture();
        var main = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = true;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

        main.Show();
        main.Width = 1100;
        main.Height = 760;
        viewModel.IsLogViewerOpen = true;
        viewModel.StartContextTutorialCommand.Execute("Logs");
        main.Measure(new Size(1100, 760));
        main.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(140);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        RenderAndAssert(main, 1100, 760, "context-logs-ru-dark-1100x760.png");

        viewModel.SkipOnboardingCommand.Execute(null);
        viewModel.IsLogViewerOpen = false;
        viewModel.IsSettingsOpen = true;
        viewModel.StartContextTutorialCommand.Execute("Settings");
        for (var step = 0; step < 4; step++)
        {
            viewModel.NextOnboardingCommand.Execute(null);
        }
        main.Width = 860;
        main.Height = 560;
        main.Measure(new Size(860, 560));
        main.Arrange(new Rect(0, 0, 860, 560));
        await Task.Delay(140);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        RenderAndAssert(main, 860, 560, "context-settings-overlay-ru-dark-860x560.png");
        viewModel.SkipOnboardingCommand.Execute(null);

        ApplyDonationAlertsSessionForTesting(viewModel);
        var donations = new DonationAlertsWindow
        {
            DataContext = viewModel,
            RequestedThemeVariant = ThemeVariant.Dark,
            Width = 520,
            Height = 620
        };
        try
        {
            donations.Show();
            viewModel.StartContextTutorialCommand.Execute("Donations");
            donations.Measure(new Size(520, 620));
            donations.Arrange(new Rect(0, 0, 520, 620));
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            RenderAndAssert(donations, 520, 620, "context-donations-ru-dark-520x620.png");
        }
        finally
        {
            viewModel.SkipOnboardingCommand.Execute(null);
            donations.DataContext = null;
            donations.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task OnboardingBlocksTheApplicationWhileKeepingTutorialControlsInteractive()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = true;

        viewModel.StartOnboardingCommand.Execute(null);
        viewModel.NextOnboardingCommand.Execute(null);
        viewModel.NextOnboardingCommand.Execute(null);
        await Task.Delay(120);
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));

        Assert.Equal(2, viewModel.OnboardingStep);
        Assert.True(viewModel.IsChannelEditorOpen);
        var blocker = AssertControl<Border>(window, "OnboardingInputBlocker");
        var dragArea = AssertControl<Border>(window, "OnboardingWindowDragArea");
        var onboardingOverlay = AssertControl<Border>(window, "OnboardingOverlay");
        var channelEditorCard = AssertControl<Border>(window, "ChannelEditorCard");
        Assert.True(onboardingOverlay.ZIndex > channelEditorCard.ZIndex);
        Assert.True(blocker.IsHitTestVisible);
        Assert.Equal(window.Bounds.Size, blocker.Bounds.Size);
        Assert.True(dragArea.ZIndex > blocker.ZIndex);
        Assert.Equal(42, dragArea.Bounds.Height, precision: 1);
        var dragHit = onboardingOverlay.InputHitTest(new Point(550, 20));
        Assert.NotNull(dragHit);
        Assert.True(
            ReferenceEquals(dragHit, dragArea) ||
            dragHit is Visual dragVisual &&
            dragVisual.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, dragArea)));
        Assert.NotNull(window.FindControl<global::Avalonia.Controls.Shapes.Path>(
            "OnboardingDimmingMask")!.Data);

        window.MouseDown(new Point(500, 500), MouseButton.Left);
        window.MouseUp(new Point(500, 500), MouseButton.Left);
        await Task.Delay(40);
        Assert.True(viewModel.IsChannelEditorOpen);

        viewModel.ToggleChannelEditorCommand.Execute(null);
        Assert.True(viewModel.IsChannelEditorOpen);

        var next = AssertControl<Button>(window, "OnboardingNextButton");
        Assert.True(next.IsHitTestVisible);
        Assert.True(next.IsEnabled);
        var nextOrigin = next.TranslatePoint(new Point(), window);
        Assert.NotNull(nextOrigin);
        var nextPoint = nextOrigin!.Value + new Vector(next.Bounds.Width / 2, next.Bounds.Height / 2);
        var hit = onboardingOverlay.InputHitTest(nextPoint);
        Assert.NotNull(hit);
        Assert.True(
            ReferenceEquals(hit, next) ||
            hit is Visual hitVisual &&
            hitVisual.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, next)));
        Assert.NotNull(next.Command);
        next.Command!.Execute(next.CommandParameter);
        await Task.Delay(80);
        Assert.Equal(3, viewModel.OnboardingStep);
        Assert.False(viewModel.IsChannelEditorOpen);
    }

    [AvaloniaFact]
    public async Task CompactModeAndPanelsUseAnimatedTransitions()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = false;

        var header = window.FindControl<Border>("HeaderPanel")!;
        var headerToggle = AssertControl<Button>(window, "HeaderToggleButton");
        headerToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => header.Transitions?.Any(
                transition => transition is global::Avalonia.Animation.DoubleTransition opacityTransition &&
                              opacityTransition.Duration > TimeSpan.Zero) == true,
            TimeSpan.FromSeconds(1));
        Assert.Contains(
            header.Transitions ?? [],
            transition => transition is global::Avalonia.Animation.DoubleTransition opacityTransition &&
                          opacityTransition.Duration > TimeSpan.Zero);
        Assert.True(header.IsVisible);
        await WaitForAsync(() => !header.IsVisible, TimeSpan.FromSeconds(2));
        Assert.False(header.IsVisible);

        headerToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => header.IsVisible && header.Opacity > 0.99,
            TimeSpan.FromSeconds(2));
        Assert.True(header.IsVisible);
        Assert.True(header.Opacity > 0.99);

        viewModel.Channel = "witherchat";
        var composer = window.FindControl<Grid>("ComposerPanel")!;
        var composerInput = AssertControl<TextBox>(window, "ComposerInput");
        var sendButton = AssertControl<Button>(window, "SendMessageButton");
        Assert.False(composerInput.AcceptsReturn);
        Assert.Equal(TextWrapping.NoWrap, composerInput.TextWrapping);
        Assert.Equal(56, composerInput.MinHeight);
        Assert.Equal(VerticalAlignment.Stretch, composerInput.VerticalAlignment);
        Assert.Equal(46, sendButton.Width);
        Assert.Equal(46, sendButton.Height);
        Assert.IsType<global::Avalonia.Controls.Shapes.Path>(sendButton.Content);
        viewModel.ComposerText = string.Concat(Enumerable.Repeat(
            "https://www.youtube.com/watch?v=dXqeb3x-1Wc",
            20));
        Assert.DoesNotContain(Environment.NewLine, viewModel.ComposerText);
        var composerToggle = AssertControl<Button>(window, "ComposerToggleButton");
        composerToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => composer.Transitions?.Any(
                transition => transition is global::Avalonia.Animation.DoubleTransition opacityTransition &&
                              opacityTransition.Duration > TimeSpan.Zero) == true,
            TimeSpan.FromSeconds(1));
        Assert.Contains(
            composer.Transitions ?? [],
            transition => transition is global::Avalonia.Animation.DoubleTransition opacityTransition &&
                          opacityTransition.Duration > TimeSpan.Zero);
        Assert.True(composer.IsVisible);
        await WaitForAsync(() => !composer.IsVisible, TimeSpan.FromSeconds(2));
        Assert.False(composer.IsVisible);
        composerToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => composer.IsVisible && composer.Opacity > 0.99,
            TimeSpan.FromSeconds(2));
        Assert.True(composer.IsVisible);
        Assert.True(composer.Opacity > 0.99);
        viewModel.Channel = string.Empty;

        var compactButton = AssertControl<Button>(window, "CompactModeButton");
        var windowVisualRoot = window.FindControl<Grid>("WindowVisualRoot")!;
        var chatLayoutRoot = window.FindControl<Grid>("ChatLayoutRoot")!;
        viewModel.IsFiltersVisible = true;
        viewModel.IsSettingsOpen = true;
        viewModel.IsConnectPanelOpen = true;
        viewModel.IsChannelEditorOpen = true;
        viewModel.IsLogViewerOpen = true;
        viewModel.IsModerationPanelOpen = true;
        viewModel.IsDeleteLogConfirmationOpen = true;
        viewModel.IsModerationDialogOpen = true;
        viewModel.IsRecentMessagesOpen = true;
        viewModel.IsAddingChannel = true;
        compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(() => viewModel.IsCompactMode, TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsCompactMode);
        Assert.False(viewModel.IsFiltersVisible);
        Assert.False(viewModel.IsSettingsOpen);
        Assert.False(viewModel.IsConnectPanelOpen);
        Assert.False(viewModel.IsChannelEditorOpen);
        Assert.False(viewModel.IsLogViewerOpen);
        Assert.False(viewModel.IsModerationPanelOpen);
        Assert.False(viewModel.IsDeleteLogConfirmationOpen);
        Assert.False(viewModel.IsModerationDialogOpen);
        Assert.False(viewModel.IsRecentMessagesOpen);
        Assert.False(viewModel.IsAddingChannel);
        Assert.Equal(1, windowVisualRoot.Opacity);
        // The headless dispatcher can resume at any point of the opacity animation.
        // The completed state is asserted after the bounds transition below.
        Assert.InRange(chatLayoutRoot.Opacity, 0, 1);
        Assert.InRange(window.Bounds.Width, 360, 1100);
        Assert.InRange(window.Bounds.Height, 400, 760);
        await WaitForAsync(
            () => compactButton.IsEnabled &&
                  !viewModel.IsMessageRenderingSuspended &&
                  Math.Abs(window.Bounds.Width - 360) < 1,
            TimeSpan.FromSeconds(3));
        Assert.True(viewModel.IsCompactMode);
        Assert.Equal(360, window.Bounds.Width, 1);
        Assert.Equal(400, window.Bounds.Height, 1);
        Assert.True(AssertControl<Border>(window, "PinnedMessageCard").IsVisible);

        var movedCompactPosition = new PixelPoint(240, 180);
        window.Position = movedCompactPosition;
        compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForAsync(
            () => compactButton.IsEnabled &&
                  !viewModel.IsCompactMode &&
                  Math.Abs(window.Bounds.Width - 1100) < 1,
            TimeSpan.FromSeconds(3));
        Assert.False(viewModel.IsCompactMode);
        Assert.Equal(1, windowVisualRoot.Opacity);
        Assert.False(viewModel.IsCompactMode);
        Assert.Equal(1100, window.Bounds.Width, 1);
        Assert.Equal(760, window.Bounds.Height, 1);
        var stableExpandedPosition = window.Position;
        viewModel.ReduceMotion = true;
        for (var cycle = 0; cycle < 4; cycle++)
        {
            compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(40);
            Assert.True(viewModel.IsCompactMode);
            compactButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(40);
            Assert.False(viewModel.IsCompactMode);
            Assert.Equal(stableExpandedPosition, window.Position);
        }
    }

    [AvaloniaFact]
    public async Task ModalPanelsMoveAndRestoreFocusAndEscapeClosesThem()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = true;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));

        var opener = AssertControl<Button>(window, "SettingsButton");
        var modalCases = new (string Card, Action Open, Func<bool> IsOpen)[]
        {
            ("SettingsCard", () => viewModel.IsSettingsOpen = true, () => viewModel.IsSettingsOpen),
            ("ConnectPanelCard", () => viewModel.IsConnectPanelOpen = true, () => viewModel.IsConnectPanelOpen),
            ("LogViewerCard", () => viewModel.IsLogViewerOpen = true, () => viewModel.IsLogViewerOpen),
            ("StreamEventsCard", () => viewModel.IsStreamEventsOpen = true, () => viewModel.IsStreamEventsOpen),
            ("ProtectionPanelCard", () => viewModel.IsProtectionPanelOpen = true, () => viewModel.IsProtectionPanelOpen),
            ("MomentsPanelCard", () => viewModel.IsMomentsPanelOpen = true, () => viewModel.IsMomentsPanelOpen),
            ("ModerationPanelCard", () => viewModel.IsModerationPanelOpen = true, () => viewModel.IsModerationPanelOpen),
            ("RecentMessagesCard", () => viewModel.IsRecentMessagesOpen = true, () => viewModel.IsRecentMessagesOpen)
        };

        foreach (var modal in modalCases)
        {
            opener.Focus();
            Assert.True(opener.IsFocused);
            modal.Open();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(1100, 760));
            window.Arrange(new Rect(0, 0, 1100, 760));
            await Task.Delay(20);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var card = Assert.IsAssignableFrom<Control>(window.FindControl<Control>(modal.Card));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(card));
            var focused = Assert.IsAssignableFrom<Control>(
                TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
            Assert.True(IsInside(focused, card), $"Focus escaped {modal.Card} when it opened.");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await WaitForAsync(() => !modal.IsOpen(), TimeSpan.FromSeconds(1));
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(opener.IsFocused, $"Focus was not restored after closing {modal.Card}.");
        }

        foreach (var cardName in new[]
                 {
                     "ChannelEditorCard",
                     "ClearChatConfirmationCard",
                     "MomentEditorCard",
                     "DeleteLogCard",
                     "ModerationDialogCard",
                     "OnboardingCard"
                 })
        {
            var card = Assert.IsAssignableFrom<Control>(window.FindControl<Control>(cardName));
            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(card));
        }

        static bool IsInside(Control control, Control card) =>
            ReferenceEquals(control, card) ||
            control.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, card));
    }

    [AvaloniaFact]
    public async Task EscapeClosesOnlyTopmostNestedModalAndRestoresParentFocus()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        viewModel.ReduceMotion = true;
        window.Show();
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));

        var opener = AssertControl<Button>(window, "ChatLogsButton");
        opener.Focus();
        viewModel.IsLogViewerOpen = true;
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var logCard = Assert.IsAssignableFrom<Control>(window.FindControl<Control>("LogViewerCard"));
        var parentFocus = Assert.IsAssignableFrom<Control>(
            TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        Assert.Contains(
            parentFocus.GetVisualAncestors(),
            ancestor => ReferenceEquals(ancestor, logCard));

        viewModel.IsDeleteLogConfirmationOpen = true;
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var deleteCard = Assert.IsAssignableFrom<Control>(window.FindControl<Control>("DeleteLogCard"));
        var nestedFocus = Assert.IsAssignableFrom<Control>(
            TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement());
        Assert.Contains(
            nestedFocus.GetVisualAncestors(),
            ancestor => ReferenceEquals(ancestor, deleteCard));

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await WaitForAsync(() => !viewModel.IsDeleteLogConfirmationOpen, TimeSpan.FromSeconds(1));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.IsLogViewerOpen);
        Assert.True(parentFocus.IsFocused);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await WaitForAsync(() => !viewModel.IsLogViewerOpen, TimeSpan.FromSeconds(1));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(opener.IsFocused);
    }

    [AvaloniaFact]
    public async Task AllOverlaysUseSymmetricPremiumOpenAndCloseAnimations()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = false;

        var panels = new (string Overlay, string Card, Action<bool> SetOpen)[]
        {
            ("ConnectPanelOverlay", "ConnectPanelCard", value => viewModel.IsConnectPanelOpen = value),
            ("LogViewerOverlay", "LogViewerCard", value => viewModel.IsLogViewerOpen = value),
            ("ModerationPanelOverlay", "ModerationPanelCard", value => viewModel.IsModerationPanelOpen = value),
            ("DeleteLogOverlay", "DeleteLogCard", value => viewModel.IsDeleteLogConfirmationOpen = value),
            ("ModerationDialogOverlay", "ModerationDialogCard", value => viewModel.IsModerationDialogOpen = value),
            ("SettingsOverlay", "SettingsCard", value => viewModel.IsSettingsOpen = value)
        };

        viewModel.ReduceMotion = true;
        foreach (var panel in panels)
        {
            panel.SetOpen(false);
        }
        await Task.Delay(40);
        viewModel.ReduceMotion = false;

        foreach (var panel in panels)
        {
            var overlay = window.FindControl<Border>(panel.Overlay);
            var card = window.FindControl<Control>(panel.Card);
            Assert.NotNull(overlay);
            Assert.NotNull(card);
            panel.SetOpen(true);
            Assert.True(overlay!.IsVisible);
            Assert.True(overlay.IsHitTestVisible);
            await WaitForAsync(
                () => overlay.Transitions?.Count > 0 && card.Transitions?.Count > 0,
                TimeSpan.FromSeconds(2));
            Assert.NotEmpty(overlay.Transitions ?? []);
            Assert.NotEmpty(card.Transitions ?? []);
            Assert.Contains(
                overlay.Transitions ?? [],
                transition => transition is global::Avalonia.Animation.DoubleTransition opacityTransition &&
                              opacityTransition.Duration > TimeSpan.Zero);
            Assert.Contains(
                card.Transitions ?? [],
                transition => transition is global::Avalonia.Animation.TransformOperationsTransition transformTransition &&
                              transformTransition.Duration > TimeSpan.Zero);
            await WaitForAsync(() => overlay.Opacity > 0.99 && card.Opacity > 0.99,
                TimeSpan.FromSeconds(2));
            Assert.True(overlay.Opacity > 0.99);
            Assert.True(card.Opacity > 0.99);

            panel.SetOpen(false);
            Assert.True(overlay.IsVisible);
            Assert.False(overlay.IsHitTestVisible);
            Assert.Contains(
                overlay.Transitions ?? [],
                transition => transition is global::Avalonia.Animation.DoubleTransition opacityTransition &&
                              opacityTransition.Duration > TimeSpan.Zero);
            Assert.Contains(
                card.Transitions ?? [],
                transition => transition is global::Avalonia.Animation.TransformOperationsTransition transformTransition &&
                              transformTransition.Duration > TimeSpan.Zero);
            await WaitForAsync(() => !overlay.IsVisible, TimeSpan.FromSeconds(2));
            Assert.False(overlay.IsVisible, $"Overlay {panel.Overlay} did not finish its close animation.");
        }
    }

    [AvaloniaFact]
    public async Task MinimizeButtonAnimatesBeforeChangingNativeWindowState()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = false;

        var visualRoot = window.FindControl<Grid>("WindowVisualRoot")!;
        var minimizeButton = new[]
            {
                AssertControl<Button>(window, "MinimizeButtonMac"),
                AssertControl<Button>(window, "MinimizeButtonWindows")
            }
            .Single(button => button.IsEffectivelyVisible);

        minimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(180);
        Assert.NotEqual(WindowState.Minimized, window.WindowState);
        Assert.Equal(1, window.Opacity);
        Assert.Single(visualRoot.Transitions!);
        Assert.Equal(1, visualRoot.Opacity);
        Assert.False(visualRoot.RenderTransform?.Value.IsIdentity ?? true);

        await Task.Delay(280);
        Assert.Equal(WindowState.Minimized, window.WindowState);
    }

    [AvaloniaFact]
    public async Task WindowControlsKeepCloseOnOuterEdgeAndCompactButtonOnInnerEdge()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        viewModel.WindowControlsStyle = "Windows";
        viewModel.WindowControlsOnRight = true;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(20);

        var compact = AssertControl<Button>(window, "CompactModeButton");
        var minimize = AssertControl<Button>(window, "MinimizeButtonWindows");
        var maximize = AssertControl<Button>(window, "MaximizeButtonWindows");
        var close = AssertControl<Button>(window, "CloseButtonWindows");
        static double Left(Control control, Visual root) =>
            control.TranslatePoint(default, root)!.Value.X;

        Assert.True(Left(compact, window) < Left(minimize, window));
        Assert.True(Left(minimize, window) < Left(maximize, window));
        Assert.True(Left(maximize, window) < Left(close, window));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(close)));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(compact)));

        viewModel.WindowControlsOnRight = false;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        await Task.Delay(20);

        Assert.True(Left(close, window) < Left(maximize, window));
        Assert.True(Left(maximize, window) < Left(minimize, window));
        Assert.True(Left(minimize, window) < Left(compact, window));
    }

    [AvaloniaFact]
    public async Task UserInputsEnforceServiceLimitsBeforeCommandsOrSearchRun()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        window.Show();

        Assert.Equal(500, AssertControl<TextBox>(window, "ComposerInput").MaxLength);
        Assert.Equal(25, AssertControl<TextBox>(window, "ChannelInput").MaxLength);
        Assert.Equal(25, AssertControl<TextBox>(window, "ConnectPanelChannelInput").MaxLength);
        Assert.Equal(25, AssertControl<TextBox>(window, "ModerationUserLoginInput").MaxLength);
        Assert.Equal(5, AssertControl<TextBox>(window, "LogViewerLimitTextBox").MaxLength);

        fixture.ViewModel.OpenConnectPanelCommand.Execute(null);
        fixture.ViewModel.ConnectPanelChannel = "invalid channel name";
        await Task.Delay(350);
        Assert.False(fixture.ViewModel.IsChannelSearchBusy);
        Assert.Empty(fixture.ViewModel.ChannelSearchResults);
        Assert.False(fixture.ViewModel.CanWatchChannel);
    }

    [AvaloniaFact]
    public async Task SwitchingToEnglishRefreshesAllComputedHeaderAndControlLabels()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName is not null)
            {
                changed.Add(eventArgs.PropertyName);
            }
        };

        viewModel.Language = "en";

        Assert.Equal("Guest", viewModel.HeaderTitle);
        Assert.Contains(nameof(viewModel.HeaderTitle), changed);
        Assert.Contains(nameof(viewModel.CompactModeTip), changed);
        Assert.Contains(nameof(viewModel.ComposerToggleTip), changed);
        Assert.Contains(nameof(viewModel.ReadOnlyComposerNotice), changed);
        Assert.Contains(nameof(viewModel.DonationAlertsObsControlStatus), changed);
        Assert.DoesNotContain("Гость", viewModel.HeaderTitle, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SignedOutSelectedChannelUsesACleanTwoLineHeader()
    {
        await using var fixture = new WindowFixture();
        var viewModel = fixture.ViewModel;
        var channelAvatar = new ChatImageResource(30);
        var channel = new ChannelSessionViewModel("wither_101")
        {
            DisplayName = "Wither_101",
            ProfileImageResource = channelAvatar
        };

        viewModel.SavedChannels.Add(channel);
        viewModel.SelectedSavedChannel = channel;

        Assert.False(viewModel.IsAccountConnected);
        Assert.Equal("Wither_101", viewModel.HeaderTitle);
        Assert.Equal("Twitch не подключён", viewModel.HeaderSubtitle);
        Assert.Empty(viewModel.ActiveChannelLabel);
        Assert.Equal("W", viewModel.AvatarInitial);
        Assert.Same(channelAvatar, viewModel.HeaderProfileImageResource);
    }

    [AvaloniaFact]
    public async Task WindowLifecycleAnimationsAndLoadingIndicatorMatchLegacy()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        window.Show();
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        viewModel.ReduceMotion = false;

        window.PrepareWindowOpenAnimation();
        var visualRoot = window.FindControl<Grid>("WindowVisualRoot")!;
        var openAnimation = window.PlayWindowOpenAnimationAsync();
        await openAnimation;
        Assert.Equal(1, window.Opacity);
        Assert.Equal(1, visualRoot.Opacity);
        Assert.Equal(TransformOperations.Identity, visualRoot.RenderTransform);

        var closeAnimation = window.AnimateWindowCloseVisualAsync();
        Assert.Single(visualRoot.Transitions!);
        await closeAnimation;
        Assert.Equal(1, window.Opacity);
        Assert.Equal(1, visualRoot.Opacity);
        Assert.False(visualRoot.RenderTransform?.Value.IsIdentity ?? true);
        window.PrepareWindowOpenAnimation();
        await window.PlayWindowOpenAnimationAsync();

        var minimizeAnimation = window.AnimateWindowMinimizeVisualAsync();
        Assert.Single(visualRoot.Transitions!);
        await Task.Delay(100);
        Assert.Equal(1, window.Opacity);
        Assert.Equal(1, visualRoot.Opacity);
        await minimizeAnimation;
        Assert.Equal(1, window.Opacity);
        Assert.Equal(1, visualRoot.Opacity);
        Assert.False(visualRoot.RenderTransform?.Value.IsIdentity ?? true);
        window.PrepareWindowOpenAnimation(offsetY: 24);
        await window.PlayWindowOpenAnimationAsync(durationMilliseconds: 280);

        var indicator = AssertControl<Border>(window, "ChatMediaLoadingIndicator");
        Assert.False(indicator.IsVisible);
        viewModel.IsChatMediaLoading = true;
        Assert.True(indicator.IsVisible);
        var progress = indicator.GetLogicalDescendants().OfType<ProgressBar>().Single();
        var label = indicator.GetLogicalDescendants().OfType<TextBlock>().Single();
        Assert.Equal(34, progress.Width);
        Assert.Equal(4, progress.Height);
        Assert.True(progress.IsIndeterminate);
        var compactIndicator = AssertControl<CompactLoadingBar>(window, "CompactChatMediaLoadingIndicator");
        viewModel.IsCompactMode = true;
        Assert.False(indicator.IsVisible);
        Assert.True(compactIndicator.IsVisible);
        Assert.Equal(148, compactIndicator.Width);
        Assert.Equal(28, compactIndicator.Height);
        Assert.Equal(HorizontalAlignment.Center, compactIndicator.HorizontalAlignment);
        Assert.NotNull(compactIndicator.TrackBrush);
        Assert.NotNull(compactIndicator.IndicatorBrush);
        Assert.Equal("Загрузка изображений…", label.Text);
    }

    private static void RenderAndAssert(Window window, int width, int height, string baselineName)
    {
        window.Width = width;
        window.Height = height;
        if (!window.IsVisible)
        {
            window.Show();
        }
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        using var settlingFrame = window.CaptureRenderedFrame();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveAuditFrame(frame, baselineName);
        Assert.Equal(width, frame!.PixelSize.Width);
        Assert.Equal(height, frame.PixelSize.Height);
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        AssertMatchesBaseline(stream.ToArray(), baselineName);
    }

    private static void AssertMatchesBaseline(byte[] actualBytes, string baselineName)
    {
        var sourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "WitherChat.Avalonia.HeadlessTests",
            "Baselines");
        var outputBaseline = Path.Combine(sourceDirectory, baselineName);
        var updateAllBaselines = string.Equals(
            Environment.GetEnvironmentVariable("WITHERCHAT_UPDATE_BASELINES"),
            "1",
            StringComparison.Ordinal);
        var updateNamedBaseline = (Environment.GetEnvironmentVariable("WITHERCHAT_UPDATE_BASELINE") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(baselineName, StringComparer.Ordinal);
        if (updateAllBaselines || updateNamedBaseline)
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllBytes(Path.Combine(sourceDirectory, baselineName), actualBytes);
            return;
        }

        Assert.True(File.Exists(outputBaseline), "Missing visual baseline: " + baselineName);
        using var expected = SKBitmap.Decode(File.ReadAllBytes(outputBaseline));
        using var actual = SKBitmap.Decode(actualBytes);
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Width, actual!.Width);
        Assert.Equal(expected.Height, actual.Height);

        double squaredDifference = 0;
        var samples = 0;
        const int columns = 80;
        const int rows = 56;
        for (var row = 0; row < rows; row++)
        {
            var y = Math.Min(actual.Height - 1, row * actual.Height / rows);
            for (var column = 0; column < columns; column++)
            {
                var x = Math.Min(actual.Width - 1, column * actual.Width / columns);
                var left = expected.GetPixel(x, y);
                var right = actual.GetPixel(x, y);
                squaredDifference += Square(left.Red - right.Red);
                squaredDifference += Square(left.Green - right.Green);
                squaredDifference += Square(left.Blue - right.Blue);
                samples += 3;
            }
        }

        var rootMeanSquareDifference = Math.Sqrt(squaredDifference / samples);
        var maximumDifference = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 8.0 : 12;
        Assert.True(
            rootMeanSquareDifference <= maximumDifference,
            $"Visual baseline {baselineName} differs too much: RMSE {rootMeanSquareDifference:0.00}.");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!predicate() && DateTime.UtcNow - startedAt < timeout)
        {
            await Task.Delay(20);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WitherChat.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
               throw new DirectoryNotFoundException("Could not locate the WitherChat repository root.");
    }

    private static int Square(int value) => value * value;

    private static RichChatTextBlock AssertProviderComposition(MainWindow window, ChatMessageItemViewModel item)
    {
        Assert.Contains(item.Parts, part => part.Provider == "Twitch");
        Assert.Contains(item.Parts, part => ThirdPartyEmoteProviders.IsBttv(part.Provider));
        Assert.Contains(item.Parts, part =>
            part.Provider == ThirdPartyEmoteProviders.SevenTv && !part.IsZeroWidth);
        Assert.Contains(item.Parts, part =>
            part.Provider == ThirdPartyEmoteProviders.SevenTv && part.IsZeroWidth);
        var richMessage = Assert.Single(
            window.GetVisualDescendants().OfType<RichChatTextBlock>(),
            control => ReferenceEquals(control.DataContext, item));
        var emoteHosts = richMessage.Inlines!
            .OfType<InlineUIContainer>()
            .Select(container => Assert.IsType<Grid>(container.Child))
            .ToArray();
        Assert.Equal(3, emoteHosts.Length);
        Assert.All(
            emoteHosts,
            host => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(host))));
        Assert.Contains(emoteHosts, host => AutomationProperties.GetName(host) == "Kappa (Twitch)");
        Assert.Contains(emoteHosts, host => AutomationProperties.GetName(host) == "BASE (7TV)");
        Assert.Contains(emoteHosts, host => AutomationProperties.GetName(host) == "OMEGALUL (BTTV)");
        Assert.Contains(emoteHosts, host => host.Children.Count == 4);
        return richMessage;
    }

    private static void ApplyDonationAlertsSessionForTesting(MainWindowViewModel viewModel)
    {
        var session = new DonationAlertsAuthSession(
            "access", DonationAlertsApplication.ClientId, 42, "streamer", "Streamer", null,
            DonationAlertsApplication.RequiredScopes, DateTimeOffset.UtcNow.AddHours(1));
        typeof(MainWindowViewModel)
            .GetMethod("ApplyDonationAlertsSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [session]);
    }

    private static Control FindMenuControl(ContextMenu menu, string name) =>
        Assert.Single(menu.Items.OfType<Control>(), control => control.Name == name);

    private static T AssertControl<T>(Control root, string automationId) where T : Control
    {
        var control = root.GetLogicalDescendants()
            .OfType<T>()
            .FirstOrDefault(item =>
                string.Equals(AutomationProperties.GetAutomationId(item), automationId, StringComparison.Ordinal));
        Assert.NotNull(control);
        return control!;
    }

    private static void ClickAtCenter(TopLevel window, Control control)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2),
            window);
        Assert.NotNull(point);
        window.MouseDown(point.Value, MouseButton.Left);
        window.MouseUp(point.Value, MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task DonationWindowShowsConnectButtonUntilAccountIsConnected()
    {
        await using var fixture = new WindowFixture();
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark
        };

        try
        {
            window.Show();
            var helpButton = AssertControl<Button>(window, "DonationHelpButton");
            await WaitForAsync(() => helpButton.IsFocused, TimeSpan.FromSeconds(1));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await WaitForAsync(() => !window.IsVisible, TimeSpan.FromSeconds(1));
            window.Show();
            await WaitForAsync(() => helpButton.IsFocused, TimeSpan.FromSeconds(1));
            var connectButton = window.FindControl<Button>("DonationWindowConnectButton")!;
            var connectStatus = AssertControl<TextBlock>(window, "DonationConnectStatus");
            Assert.False(fixture.ViewModel.IsDonationAlertsConnected);
            Assert.True(connectButton.IsEffectivelyVisible);
            Assert.True(connectButton.IsEnabled);
            Assert.False(connectStatus.IsVisible);
            fixture.ViewModel.DonationAlertsStatus = "Проверяем соединение";
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(connectStatus.IsVisible);
            fixture.ViewModel.DonationAlertsStatus = fixture.ViewModel.Texts.DonationAlertsNotConnected;
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.False(connectStatus.IsVisible);
            Assert.True(window.CanResize);
            AssertControl<Button>(window, "DonationWindowMinimizeButton");
            AssertControl<Border>(window, "DonationWindowDragArea");
            var resizeMarker = AssertControl<global::Avalonia.Controls.Shapes.Path>(
                window,
                "DonationWindowResizeMarker");
            Assert.False(resizeMarker.IsHitTestVisible);
            Assert.Equal(
                WindowEdge.NorthWest,
                DonationAlertsWindow.ResizeEdgeAt(new Point(2, 2), new Size(520, 620)));
            Assert.Equal(
                WindowEdge.East,
                DonationAlertsWindow.ResizeEdgeAt(new Point(518, 300), new Size(520, 620)));
            Assert.Equal(
                WindowEdge.South,
                DonationAlertsWindow.ResizeEdgeAt(new Point(260, 618), new Size(520, 620)));
            Assert.Null(
                DonationAlertsWindow.ResizeEdgeAt(new Point(260, 300), new Size(520, 620)));
            RenderAndAssert(window, 520, 620, "donation-alert-disconnected-ru-dark-520x620.png");

            var session = new DonationAlertsAuthSession(
                "access", DonationAlertsApplication.ClientId, 42, "streamer", "Streamer", null,
                DonationAlertsApplication.RequiredScopes, DateTimeOffset.UtcNow.AddHours(1));
            typeof(MainWindowViewModel)
                .GetMethod("ApplyDonationAlertsSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.ViewModel, [session]);
            RenderAndAssert(window, 520, 620, "donation-alert-connected-ru-dark-520x620.png");

            Assert.True(fixture.ViewModel.IsDonationAlertsConnected);
            Assert.False(connectButton.IsEffectivelyVisible);
            var refreshButton = AssertControl<Button>(window, "RefreshDonationHistoryButton");
            Assert.True(refreshButton.IsEnabled);
            ClickAtCenter(window, refreshButton);
            await WaitForAsync(
                () => fixture.ViewModel.HasDonationHistoryError,
                TimeSpan.FromSeconds(2));
            Assert.Equal("Подключён аккаунт: Streamer", fixture.ViewModel.DonationAlertsAccountLabel);
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task DonationHistoryLoadingOverlayUsesTranslucentGlassStyle()
    {
        var handler = new ControlledDonationHistoryHandler(blockFirstRequest: true);
        await using var fixture = new WindowFixture(donationAlertsHandler: handler);
        ApplyDonationAlertsSessionForTesting(fixture.ViewModel);
        typeof(MainWindowViewModel)
            .GetMethod("ApplyDonationHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.ViewModel,
            [
                new[]
                {
                    new DonationAlert(
                        "loading-card", "Viewer", "Donation beneath the loading overlay",
                        100, "RUB", DateTimeOffset.UtcNow)
                }
            ]);
        fixture.ViewModel.RefreshDonationHistoryCommand.Execute(null);
        await WaitForAsync(() => handler.RequestCount == 1, TimeSpan.FromSeconds(2));
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark
        };

        try
        {
            window.Show();
            window.Measure(new Size(520, 620));
            window.Arrange(new Rect(0, 0, 520, 620));

            var overlay = AssertControl<Border>(window, "DonationHistoryLoadingOverlay");
            var progress = AssertControl<ProgressBar>(window, "DonationHistoryLoadingProgress");
            var headerProgress = AssertControl<ProgressBar>(window, "DonationHistoryHeaderProgress");
            var overlayBackground = Assert.IsAssignableFrom<ISolidColorBrush>(overlay.Background);
            var progressBackground = Assert.IsAssignableFrom<ISolidColorBrush>(progress.Background);
            var progressForeground = Assert.IsAssignableFrom<ISolidColorBrush>(progress.Foreground);

            Assert.Equal(224, overlay.Width);
            Assert.Equal(new CornerRadius(12), overlay.CornerRadius);
            Assert.Equal(new Thickness(1), overlay.BorderThickness);
            Assert.False(overlay.IsHitTestVisible);
            Assert.True(overlay.IsEffectivelyVisible);
            Assert.InRange(overlayBackground.Color.A, (byte)1, (byte)254);
            Assert.Equal(156, progress.Width);
            Assert.InRange(progress.Bounds.Width, 155, 157);
            Assert.Equal(3, progress.Height);
            Assert.True(progress.IsIndeterminate);
            Assert.InRange(progressBackground.Color.A, (byte)1, (byte)254);
            Assert.Equal(Color.Parse("#FF9D45"), progressForeground.Color);
            Assert.InRange(headerProgress.Bounds.Width, 45, 47);
        }
        finally
        {
            handler.ReleaseFirstRequest();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DonationHistoryRefreshCoalescesRepeatedClicksAndKeepsButtonAvailable()
    {
        var handler = new ControlledDonationHistoryHandler(blockFirstRequest: true);
        await using var fixture = new WindowFixture(donationAlertsHandler: handler);
        ApplyDonationAlertsSessionForTesting(fixture.ViewModel);

        fixture.ViewModel.RefreshDonationHistoryCommand.Execute(null);
        await WaitForAsync(() => handler.RequestCount == 1, TimeSpan.FromSeconds(2));
        Assert.True(fixture.ViewModel.IsDonationHistoryLoading);
        Assert.True(fixture.ViewModel.RefreshDonationHistoryCommand.CanExecute(null));

        for (var index = 0; index < 200; index++)
        {
            fixture.ViewModel.RefreshDonationHistoryCommand.Execute(null);
        }

        handler.ReleaseFirstRequest();
        await WaitForAsync(
            () => handler.RequestCount == 2 && !fixture.ViewModel.IsDonationHistoryLoading,
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, handler.RequestCount);
        Assert.Single(fixture.ViewModel.DonationHistory);
        Assert.True(fixture.ViewModel.RefreshDonationHistoryCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.HasDonationHistoryError);
    }

    [AvaloniaFact]
    public async Task DonationHistoryRefreshTimeoutRestoresButtonAndAllowsRetry()
    {
        var handler = new ControlledDonationHistoryHandler(hangUntilCancelled: true);
        await using var fixture = new WindowFixture(donationAlertsHandler: handler);
        fixture.ViewModel.DonationHistoryRefreshTimeoutOverride = TimeSpan.FromMilliseconds(100);
        ApplyDonationAlertsSessionForTesting(fixture.ViewModel);

        fixture.ViewModel.RefreshDonationHistoryCommand.Execute(null);
        await WaitForAsync(
            () => fixture.ViewModel.HasDonationHistoryError &&
                  !fixture.ViewModel.IsDonationHistoryLoading,
            TimeSpan.FromSeconds(2));

        Assert.Contains("DonationAlerts", fixture.ViewModel.DonationHistoryError, StringComparison.Ordinal);
        Assert.True(fixture.ViewModel.RefreshDonationHistoryCommand.CanExecute(null));

        handler.HangUntilCancelled = false;
        fixture.ViewModel.RefreshDonationHistoryCommand.Execute(null);
        await WaitForAsync(
            () => fixture.ViewModel.DonationHistory.Count == 1 &&
                  !fixture.ViewModel.IsDonationHistoryLoading,
            TimeSpan.FromSeconds(2));

        Assert.Equal(2, handler.RequestCount);
        Assert.False(fixture.ViewModel.HasDonationHistoryError);
        Assert.True(fixture.ViewModel.RefreshDonationHistoryCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task DonationHistorySupportsMiddleClickScrollingWithoutVisibleScrollBar()
    {
        await using var fixture = new WindowFixture();
        for (var index = 1; index <= 40; index++)
        {
            fixture.ViewModel.EnqueueDonationForTesting(new DonationAlert(
                "history-" + index,
                "Viewer " + index,
                "Donation message " + index,
                index,
                "RUB",
                DateTimeOffset.UtcNow.AddMinutes(-index)));
        }
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark,
            Width = 520,
            Height = 620
        };

        try
        {
            window.Show();
            window.Measure(new Size(520, 620));
            window.Arrange(new Rect(0, 0, 520, 620));
            using var frame = window.CaptureRenderedFrame();
            await Task.Delay(50);

            var scrollViewer = AssertControl<ScrollViewer>(window, "DonationHistoryScrollViewer");
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
            var verticalScrollBar = scrollViewer.GetVisualDescendants()
                .OfType<global::Avalonia.Controls.Primitives.ScrollBar>()
                .Single(scrollBar => scrollBar.Orientation == Orientation.Vertical);
            Assert.False(verticalScrollBar.IsVisible);
            Assert.False(verticalScrollBar.IsEffectivelyVisible);
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                scrollViewer.VerticalScrollBarVisibility);

            var middleScrollPoint = scrollViewer.TranslatePoint(new Point(90, 120), window);
            Assert.NotNull(middleScrollPoint);
            var offsetBefore = scrollViewer.Offset.Y;
            window.MouseDown(middleScrollPoint.Value, MouseButton.Middle);
            await Task.Delay(20);
            var indicator = AssertControl<Border>(window, "DonationMiddleScrollIndicator");
            Assert.True(indicator.IsVisible);

            var movedPoint = middleScrollPoint.Value.WithY(middleScrollPoint.Value.Y + 140);
            window.MouseMove(movedPoint);
            await WaitForAsync(
                () => scrollViewer.Offset.Y > offsetBefore,
                TimeSpan.FromSeconds(1));
            Assert.True(scrollViewer.Offset.Y > offsetBefore);

            window.MouseDown(movedPoint, MouseButton.Middle);
            await Task.Delay(20);
            Assert.False(indicator.IsVisible);

            offsetBefore = scrollViewer.Offset.Y;
            Assert.True(window.ScrollDonationHistoryWithMiddleButtonForTesting(140));
            Assert.True(scrollViewer.Offset.Y > offsetBefore);
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task DonationWindowShowsQueueAndSkipImmediatelyAdvancesIt()
    {
        await using var fixture = new WindowFixture();
        var first = new DonationAlert(
            "donation-1", "First viewer", "First message", 500, "RUB", DateTimeOffset.UtcNow);
        var second = new DonationAlert(
            "donation-2", "Second viewer", "Second message", 25, "USD", DateTimeOffset.UtcNow);
        fixture.ViewModel.EnqueueDonationForTesting(first);
        fixture.ViewModel.EnqueueDonationForTesting(second);
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark
        };

        try
        {
            window.Show();
            RenderAndAssert(window, 520, 620, "donation-alert-window-ru-dark-520x620.png");

            Assert.True(fixture.ViewModel.HasCurrentDonation);
            Assert.Equal(2, fixture.ViewModel.DonationHistory.Count);
            Assert.Equal("First viewer", fixture.ViewModel.CurrentDonationUsername);
            Assert.Equal("First message", fixture.ViewModel.CurrentDonationMessage);
            Assert.Equal(1, fixture.ViewModel.PendingDonationCount);
            var skipButton = AssertControl<Button>(window, "SkipDonationButton");
            Assert.True(skipButton.IsEffectivelyVisible);
            Assert.True(skipButton.Bounds.Width > 0 && skipButton.Bounds.Height > 0);
            var skipButtonOrigin = skipButton.TranslatePoint(default, window);
            Assert.NotNull(skipButtonOrigin);
            Assert.InRange(
                skipButtonOrigin.Value.Y,
                0,
                window.Bounds.Height - skipButton.Bounds.Height);
            ClickAtCenter(window, skipButton);

            Assert.Equal("Second viewer", fixture.ViewModel.CurrentDonationUsername);
            Assert.Equal("Second message", fixture.ViewModel.CurrentDonationMessage);
            Assert.Equal(0, fixture.ViewModel.PendingDonationCount);

            fixture.ViewModel.DonationDisplayDurationOverride = TimeSpan.FromMilliseconds(250);
            var replayButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.Command, fixture.ViewModel.ReplayDonationCommand) &&
                    button.CommandParameter is DonationHistoryItemViewModel { Username: "First viewer" });
            ClickAtCenter(window, replayButton);
            Assert.True(fixture.ViewModel.HasCurrentDonation);
            Assert.Equal("First viewer", fixture.ViewModel.CurrentDonationUsername);
            Assert.Equal("First message", fixture.ViewModel.CurrentDonationMessage);
            Assert.Equal(0, fixture.ViewModel.PendingDonationCount);
            Assert.Equal(2, fixture.ViewModel.DonationHistory.Count);
            Assert.True(fixture.ViewModel.DonationDisplayProgress > 0);
            Assert.True(AssertControl<Border>(window, "CurrentDonationPresentation").IsEffectivelyVisible);
            await WaitForAsync(
                () => !fixture.ViewModel.HasCurrentDonation,
                TimeSpan.FromSeconds(2));
            Assert.Equal(0, fixture.ViewModel.DonationDisplayProgress);

            var closeButton = AssertControl<Button>(window, "DonationWindowCloseButton");
            ClickAtCenter(window, closeButton);
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task DonationWindowReplayAndSkipControlTheDonationAlertsObsWidget()
    {
        var controller = new RecordingDonationAlertsObsController();
        await using var fixture = new WindowFixture(donationAlertsObsController: controller);
        var donation = new DonationAlert(
            "30530030", "OBS viewer", "OBS message", 500, "RUB", DateTimeOffset.UtcNow);
        fixture.ViewModel.EnqueueDonationForTesting(donation);
        controller.RaiseStarted(donation);
        await WaitForAsync(() => fixture.ViewModel.HasCurrentDonation, TimeSpan.FromSeconds(2));
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark
        };

        try
        {
            window.Show();
            var skipButton = AssertControl<Button>(window, "SkipDonationButton");
            ClickAtCenter(window, skipButton);
            await WaitForAsync(() => controller.SkippedDonationIds.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Equal("30530030", controller.SkippedDonationIds.Single());
            Assert.False(fixture.ViewModel.HasCurrentDonation);

            var replayButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.Command, fixture.ViewModel.ReplayDonationCommand) &&
                    button.CommandParameter is DonationHistoryItemViewModel { Username: "OBS viewer" });
            ClickAtCenter(window, replayButton);
            await WaitForAsync(() => controller.RepeatedDonationIds.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Equal("30530030", controller.RepeatedDonationIds.Single());
            Assert.True(fixture.ViewModel.HasCurrentDonation);
            Assert.False(fixture.ViewModel.HasDonationControlError);
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task DuplicateRealtimeDonationIdCannotPlayAgainAfterCompletion()
    {
        var controller = new RecordingDonationAlertsObsController();
        await using var fixture = new WindowFixture(donationAlertsObsController: controller);
        var donation = new DonationAlert(
            "30530033", "Duplicate viewer", "Only once", 100, "RUB", DateTimeOffset.UtcNow);

        fixture.ViewModel.EnqueueDonationForTesting(donation);
        controller.RaiseStarted(donation);
        await WaitForAsync(() => fixture.ViewModel.CanHideDonation, TimeSpan.FromSeconds(2));
        controller.RaiseSkipped(donation);
        await WaitForAsync(
            () => fixture.ViewModel.DonationPlaybackState == DonationPlaybackState.Idle,
            TimeSpan.FromSeconds(2));

        fixture.ViewModel.EnqueueDonationForTesting(donation);
        await Task.Delay(50);

        Assert.Equal(DonationPlaybackState.Idle, fixture.ViewModel.DonationPlaybackState);
        Assert.False(fixture.ViewModel.HasCurrentDonation);
        Assert.Equal(0, fixture.ViewModel.PendingDonationCount);
        Assert.Single(fixture.ViewModel.DonationHistory);
    }

    [AvaloniaFact]
    public async Task DonationLoadedFromHistoryStillPlaysOnceWhenRealtimeEventArrivesLater()
    {
        var controller = new RecordingDonationAlertsObsController();
        await using var fixture = new WindowFixture(donationAlertsObsController: controller);
        var donation = new DonationAlert(
            "30530035", "History viewer", "History arrived first", 125, "RUB", DateTimeOffset.UtcNow);
        typeof(MainWindowViewModel)
            .GetMethod("ApplyDonationHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.ViewModel, [new[] { donation }]);

        Assert.Single(fixture.ViewModel.DonationHistory);
        Assert.False(fixture.ViewModel.HasCurrentDonation);

        fixture.ViewModel.EnqueueDonationForTesting(donation);
        controller.RaiseStarted(donation);
        await WaitForAsync(
            () => fixture.ViewModel.CanHideDonation &&
                  fixture.ViewModel.CurrentDonation?.Id == donation.Id,
            TimeSpan.FromSeconds(2));

        Assert.Single(fixture.ViewModel.DonationHistory);
        controller.RaiseSkipped(donation);
        await WaitForAsync(
            () => fixture.ViewModel.DonationPlaybackState == DonationPlaybackState.Idle,
            TimeSpan.FromSeconds(2));

        fixture.ViewModel.EnqueueDonationForTesting(donation);
        await Task.Delay(50);
        Assert.Equal(DonationPlaybackState.Idle, fixture.ViewModel.DonationPlaybackState);
        Assert.False(fixture.ViewModel.HasCurrentDonation);
    }

    [AvaloniaFact]
    public async Task NumericLiveDonationFallsBackLocallyWhenDirectControlIsUnavailable()
    {
        var controller = new UnavailableDonationAlertsObsController();
        await using var fixture = new WindowFixture(donationAlertsObsController: controller);
        var donation = new DonationAlert(
            "30530034", "Fallback viewer", "Still visible", 75, "RUB", DateTimeOffset.UtcNow);

        fixture.ViewModel.EnqueueDonationForTesting(donation);

        Assert.True(fixture.ViewModel.HasCurrentDonation);
        Assert.Equal(donation.Id, fixture.ViewModel.CurrentDonation?.Id);
        Assert.True(fixture.ViewModel.CanHideDonation);
        Assert.Equal(DonationPlaybackState.Idle, fixture.ViewModel.DonationPlaybackState);
        Assert.Empty(controller.RepeatedDonationIds);
    }

    [AvaloniaFact]
    public async Task DonationButtonsRecoverWithoutChangingLocalStateWhenObsControlFails()
    {
        var controller = new RecordingDonationAlertsObsController
        {
            SkipException = new InvalidOperationException("Widget did not confirm skip."),
            RepeatException = new InvalidOperationException("Widget rejected replay.")
        };
        await using var fixture = new WindowFixture(donationAlertsObsController: controller);
        var donation = new DonationAlert(
            "30530031", "OBS viewer", "OBS message", 500, "RUB", DateTimeOffset.UtcNow);
        fixture.ViewModel.EnqueueDonationForTesting(donation);
        controller.RaiseStarted(donation);
        await WaitForAsync(() => fixture.ViewModel.HasCurrentDonation, TimeSpan.FromSeconds(2));
        var window = new DonationAlertsWindow
        {
            DataContext = fixture.ViewModel,
            RequestedThemeVariant = ThemeVariant.Dark
        };

        try
        {
            window.Show();
            var skipButton = AssertControl<Button>(window, "SkipDonationButton");
            ClickAtCenter(window, skipButton);
            await WaitForAsync(
                () => fixture.ViewModel.HasDonationControlError,
                TimeSpan.FromSeconds(2));

            Assert.True(fixture.ViewModel.HasCurrentDonation);
            Assert.False(fixture.ViewModel.IsDonationControlBusy);
            Assert.True(skipButton.IsEnabled);

            controller.SkipException = null;
            ClickAtCenter(window, skipButton);
            await WaitForAsync(
                () => !fixture.ViewModel.HasCurrentDonation,
                TimeSpan.FromSeconds(2));

            var replayButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button =>
                    ReferenceEquals(button.Command, fixture.ViewModel.ReplayDonationCommand) &&
                    button.CommandParameter is DonationHistoryItemViewModel { Username: "OBS viewer" });
            ClickAtCenter(window, replayButton);
            await WaitForAsync(
                () => fixture.ViewModel.HasDonationControlError,
                TimeSpan.FromSeconds(2));

            Assert.False(fixture.ViewModel.HasCurrentDonation);
            Assert.False(fixture.ViewModel.IsDonationControlBusy);
            Assert.True(replayButton.IsEnabled);

            controller.RepeatException = null;
            ClickAtCenter(window, replayButton);
            await WaitForAsync(
                () => fixture.ViewModel.HasCurrentDonation,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task DonationControlsCoalesceTwoHundredRapidClicksIntoOneServerCommand()
    {
        var controller = new RecordingDonationAlertsObsController
        {
            SkipGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var fixture = new WindowFixture(donationAlertsObsController: controller);
        var donation = new DonationAlert(
            "30530032", "Stress viewer", "Stress message", 500, "RUB", DateTimeOffset.UtcNow);
        fixture.ViewModel.EnqueueDonationForTesting(donation);
        controller.RaiseStarted(donation);
        await WaitForAsync(() => fixture.ViewModel.CanHideDonation, TimeSpan.FromSeconds(2));

        for (var index = 0; index < 200; index++)
        {
            fixture.ViewModel.SkipDonationCommand.Execute(null);
        }

        await WaitForAsync(() => controller.SkippedDonationIds.Count == 1, TimeSpan.FromSeconds(2));
        Assert.True(fixture.ViewModel.HasCurrentDonation);
        controller.SkipGate.SetResult();
        await WaitForAsync(() => !fixture.ViewModel.IsDonationControlBusy, TimeSpan.FromSeconds(2));
        Assert.Single(controller.SkippedDonationIds);
        Assert.False(fixture.ViewModel.HasCurrentDonation);

        controller.RepeatGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = Assert.Single(fixture.ViewModel.DonationHistory);
        for (var index = 0; index < 200; index++)
        {
            fixture.ViewModel.ReplayDonationCommand.Execute(item);
        }

        await WaitForAsync(() => controller.RepeatedDonationIds.Count == 1, TimeSpan.FromSeconds(2));
        Assert.False(fixture.ViewModel.IsDonationControlBusy);
        Assert.Equal(DonationPlaybackState.WaitingForServerStart, fixture.ViewModel.DonationPlaybackState);
        Assert.False(fixture.ViewModel.HasCurrentDonation);
        controller.RepeatGate.SetResult();
        await WaitForAsync(() => fixture.ViewModel.HasCurrentDonation, TimeSpan.FromSeconds(2));
        Assert.Single(controller.RepeatedDonationIds);
        Assert.True(fixture.ViewModel.HasCurrentDonation);
    }

    [AvaloniaFact]
    public async Task ExperimentalPanelsSmartFiltersAndMomentsAreInteractive()
    {
        await using var fixture = new WindowFixture();
        var window = fixture.Window;
        var viewModel = fixture.ViewModel;
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        viewModel.Channel = "channel";
        viewModel.IsFiltersVisible = true;
        window.Show();
        window.Width = 1100;
        window.Height = 760;

        var timestamp = DateTimeOffset.UtcNow;
        viewModel.EnqueueMessageForTesting(new ChatMessage
        {
            Id = "question",
            Channel = "channel",
            UserLogin = "viewer1",
            DisplayName = "Viewer 1",
            Text = "Когда начинаем?",
            Timestamp = timestamp
        });
        viewModel.EnqueueMessageForTesting(new ChatMessage
        {
            Id = "plain",
            Channel = "channel",
            UserLogin = "viewer2",
            DisplayName = "Viewer 2",
            Text = "Просто сообщение",
            Timestamp = timestamp.AddMilliseconds(1)
        });
        var paidEvent = new StreamEvent
        {
            Id = "paid-1",
            Platform = ChatPlatforms.Twitch,
            Channel = "channel",
            Kind = StreamEventKinds.Cheer,
            DisplayName = "Member",
            Message = "Support",
            AmountDisplay = "100 RUB",
            AmountMicros = 100_000_000,
            Count = 100,
            Timestamp = timestamp.AddMilliseconds(2)
        };
        viewModel.EnqueueMessageForTesting(TwitchEventSubClient.ToSystemMessage(paidEvent));
        viewModel.EnqueueStreamEventForTesting(paidEvent);
        await WaitForAsync(() => viewModel.Messages.Count == 3 && viewModel.StreamEvents.Count == 1,
            TimeSpan.FromSeconds(2));
        Assert.Equal(3, viewModel.Messages.Count);

        ClickAtCenter(window, AssertControl<Button>(window, "SmartChatQuestionsButton"));
        await WaitForAsync(() => viewModel.VisibleMessages.Count == 1, TimeSpan.FromSeconds(1));
        Assert.Equal("question", Assert.Single(viewModel.VisibleMessages).Message.Id);

        viewModel.SetChatViewModeCommand.Execute("3");
        Assert.True(Assert.Single(viewModel.VisibleMessages).IsPaidEvent);

        viewModel.OpenStreamEventsCommand.Execute(null);
        Assert.True(AssertControl<Border>(window, "StreamEventsOverlay").IsVisible);
        RenderAndAssert(window, 1100, 760, "experimental-events-ru-dark-1100x760.png");
        viewModel.OpenProtectionPanelCommand.Execute(null);
        Assert.True(AssertControl<Border>(window, "ProtectionPanelOverlay").IsVisible);
        Assert.False(AssertControl<Border>(window, "StreamEventsOverlay").IsVisible);
        RenderAndAssert(window, 1100, 760, "experimental-protection-ru-dark-1100x760.png");

        var protectionCard = window.FindControl<Border>("ProtectionPanelCard")!;
        Assert.InRange(protectionCard.Bounds.Width, 818, 822);
        Assert.Equal(
            window.FindControl<Button>("ProtectionHelpButton")!.Bounds.Height,
            AssertControl<Button>(window, "CloseProtectionButton").Bounds.Height,
            precision: 1);
        var protectionHeader = AssertControl<Grid>(window, "ProtectionPanelFixedHeader");
        var protectionScroll = AssertControl<ScrollViewer>(window, "ProtectionPanelScrollViewer");
        Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            protectionScroll.VerticalScrollBarVisibility);
        var helpButton = window.FindControl<Button>("ProtectionHelpButton")!;
        var closeButton = AssertControl<Button>(window, "CloseProtectionButton");

        window.Width = 860;
        window.Height = 560;
        window.Measure(new Size(860, 560));
        window.Arrange(new Rect(0, 0, 860, 560));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var helpBeforeScroll = helpButton.TranslatePoint(new Point(), protectionCard);
        var closeBeforeScroll = closeButton.TranslatePoint(new Point(), protectionCard);
        Assert.NotNull(helpBeforeScroll);
        Assert.NotNull(closeBeforeScroll);
        protectionScroll.Offset = new Vector(0, protectionScroll.Extent.Height);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(helpBeforeScroll, helpButton.TranslatePoint(new Point(), protectionCard));
        Assert.Equal(closeBeforeScroll, closeButton.TranslatePoint(new Point(), protectionCard));
        Assert.True(helpBeforeScroll.Value.Y >= 0);
        Assert.True(closeBeforeScroll.Value.Y >= 0);
        Assert.True(helpBeforeScroll.Value.Y + helpButton.Bounds.Height <= protectionCard.Bounds.Height);
        Assert.True(closeBeforeScroll.Value.Y + closeButton.Bounds.Height <= protectionCard.Bounds.Height);
        Assert.True(protectionHeader.Bounds.Height >= Math.Max(helpButton.Bounds.Height, closeButton.Bounds.Height));

        protectionScroll.Offset = new Vector(0, 0);
        window.Width = 1100;
        window.Height = 760;
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var slowToggle = AssertControl<CheckBox>(window, "ProtectionSlowModeToggle");
        var slowInput = AssertControl<TextBox>(window, "ProtectionSlowSecondsInput");
        var followerToggle = AssertControl<CheckBox>(window, "ProtectionFollowerModeToggle");
        var followerInput = AssertControl<TextBox>(window, "ProtectionFollowerMinutesInput");
        Assert.False(slowInput.IsEnabled);
        Assert.False(followerInput.IsEnabled);
        ClickAtCenter(window, slowToggle);
        ClickAtCenter(window, followerToggle);
        Assert.True(slowInput.IsEnabled);
        Assert.True(followerInput.IsEnabled);
        Assert.Equal(slowInput.Bounds.Width, followerInput.Bounds.Width, precision: 1);
        ClickAtCenter(window, slowToggle);
        ClickAtCenter(window, followerToggle);

        Assert.Contains("настоящий чат", AssertControl<TextBlock>(window,
            "ProtectionTwitchSectionDescription").Text);
        Assert.Contains("лог", AssertControl<TextBlock>(window,
            "ProtectionPauseDisplayDescription").Text);
        Assert.Contains("OBS", AssertControl<TextBlock>(window,
            "ProtectionSuppressObsDescription").Text);
        var clearHint = AssertControl<TextBlock>(window, "ProtectionClearChatHint");
        var applyHint = AssertControl<TextBlock>(window, "ProtectionApplyHint");
        Assert.Equal(TextAlignment.Left, clearHint.TextAlignment);
        Assert.Equal(TextAlignment.Left, applyHint.TextAlignment);
        Assert.Equal(clearHint.Bounds.Y, applyHint.Bounds.Y, precision: 1);
        Assert.Equal(
            AssertControl<Button>(window, "ProtectionClearChatButton").Bounds.Width,
            AssertControl<Button>(window, "ProtectionApplyButton").Bounds.Width,
            precision: 1);

        var message = viewModel.Messages.First(item => item.Message.Id == "question");
        viewModel.BeginSaveMomentCommand.Execute(message);
        Assert.True(AssertControl<Border>(window, "MomentEditorOverlay").IsVisible);
        viewModel.MomentNote = "clip";
        viewModel.ConfirmSaveMomentCommand.Execute(null);
        await WaitForAsync(() => viewModel.Moments.Count == 1, TimeSpan.FromSeconds(1));
        Assert.Equal("clip", Assert.Single(viewModel.Moments).Note);
        viewModel.OpenMomentsPanelCommand.Execute(null);
        Assert.True(AssertControl<Border>(window, "MomentsPanelOverlay").IsVisible);
        RenderAndAssert(window, 1100, 760, "experimental-moments-ru-dark-1100x760.png");
    }

    private sealed class WindowFixture : IAsyncDisposable
    {
        private readonly string _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "WitherChat-headless-" + Guid.NewGuid().ToString("N"));

        public WindowFixture(
            IWitherChatClient? chatClient = null,
            HttpMessageHandler? apiHandler = null,
            IDonationAlertsObsController? donationAlertsObsController = null,
            HttpMessageHandler? donationAlertsHandler = null,
            HttpMessageHandler? youTubeHandler = null,
            HttpMessageHandler? authHandler = null,
            StreamMomentStore? streamMomentStore = null)
        {
            var paths = new AppDataPaths(_dataDirectory);
            var settingsStore = new SettingsStore(paths);
            var apiClient = new TwitchChatApiClient(handler: apiHandler ?? new StubTwitchHandler());
            var youTubeAuthService = new YouTubeAuthService(new MissingImageHandler());
            var youTubeSessionStore = new YouTubeAuthSessionStore(paths, new MemoryTokenStore());
            var youTubeLiveChatClient = new YouTubeLiveChatClient(
                youTubeAuthService,
                youTubeHandler ?? new MissingImageHandler());
            var donationAlertsAuthService = new DonationAlertsAuthService(new MissingImageHandler());
            var donationAlertsSessionStore = new DonationAlertsAuthSessionStore(paths, new MemoryTokenStore());
            var donationAlertsClient = new DonationAlertsClient(
                donationAlertsHandler ?? new MissingImageHandler());
            YouTubeLiveChatClient = youTubeLiveChatClient;
            var settings = new WitherChatSettings
            {
                ReduceMotion = true
            };
            ViewModel = new MainWindowViewModel(
                chatClient ?? new TwitchIrcClient(),
                new TwitchAuthService(handler: authHandler),
                new TwitchAuthSessionStore(paths, new MemoryTokenStore()),
                apiClient,
                new TwitchEventSubClient(apiClient),
                settingsStore,
                new ChatLogWriter(paths.LogDirectory),
                new ObsOverlayServer(),
                new ModerationCacheStore(paths),
                settings,
                new TestPlatformDescriptor(),
                youTubeAuthService,
                youTubeSessionStore,
                youTubeLiveChatClient,
                donationAlertsAuthService,
                donationAlertsSessionStore,
                donationAlertsClient,
                donationAlertsObsController,
                streamMomentStore);
            ImageCache = new ChatImageCache();
            Window = new MainWindow
            {
                DataContext = ViewModel
            };
        }

        public MainWindowViewModel ViewModel { get; }
        public ChatImageCache ImageCache { get; }
        public YouTubeLiveChatClient YouTubeLiveChatClient { get; }
        public MainWindow Window { get; }

        public async ValueTask DisposeAsync()
        {
            Window.DataContext = null;
            Window.Content = null;
            Window.Close();
            await ViewModel.DisposeAsync();
            ImageCache.Dispose();
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
    }

    private sealed class MissingImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class ControlledTwitchClipHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _clipRequestCount;

        public int ClipRequestCount => Volatile.Read(ref _clipRequestCount);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/helix/users")
            {
                return Json(
                    """{"data":[{"id":"live-id","login":"live_channel","display_name":"Live Channel","profile_image_url":""}]}""");
            }

            if (request.RequestUri?.AbsolutePath == "/helix/clips" &&
                request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _clipRequestCount);
                await _release.Task.WaitAsync(cancellationToken);
                return Json(
                    """{"data":[{"id":"HeadlessClipSlug","edit_url":"https://www.twitch.tv/live_channel/clip/HeadlessClipSlug"}]}""",
                    HttpStatusCode.Accepted);
            }

            if (request.RequestUri?.AbsolutePath == "/helix/clips" &&
                request.Method == HttpMethod.Get)
            {
                return Json("""{"data":[{"url":"https://clips.twitch.tv/HeadlessClipSlug"}]}""");
            }

            if (request.RequestUri?.AbsolutePath == "/helix/streams")
            {
                return Json("""{"data":[{"user_id":"live-id","viewer_count":321}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class RecordingYouTubeModerationHandler : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentBag<string> DeletedMessageIds { get; } = [];
        public System.Collections.Concurrent.ConcurrentBag<string> RemovedBanIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/liveBroadcasts", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(
                    """{"items":[{"id":"video-1","snippet":{"title":"Live now","liveChatId":"chat-1"}}]}"""));
            }
            if (path.EndsWith("/liveChat/messages", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json("""{"pollingIntervalMillis":30000,"items":[]}"""));
            }
            if (path.EndsWith("/liveChat/messages", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                var id = QueryValue(request.RequestUri!, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    DeletedMessageIds.Add(id);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (path.EndsWith("/liveChat/bans", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Json("""{"id":"ban-1"}"""));
            }
            if (path.EndsWith("/liveChat/bans", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
            {
                var id = QueryValue(request.RequestUri!, "id");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    RemovedBanIds.Add(id);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        private static string QueryValue(Uri uri, string key) => uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .Where(value => value.Length == 2)
            .Where(value => string.Equals(Uri.UnescapeDataString(value[0]), key, StringComparison.Ordinal))
            .Select(value => Uri.UnescapeDataString(value[1]))
            .FirstOrDefault() ?? string.Empty;
    }

    private sealed class ControlledDonationHistoryHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstRequestRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstRequest;
        private int _requestCount;
        private int _hangUntilCancelled;

        public ControlledDonationHistoryHandler(
            bool blockFirstRequest = false,
            bool hangUntilCancelled = false)
        {
            _blockFirstRequest = blockFirstRequest;
            _hangUntilCancelled = hangUntilCancelled ? 1 : 0;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool HangUntilCancelled
        {
            get => Volatile.Read(ref _hangUntilCancelled) != 0;
            set => Volatile.Write(ref _hangUntilCancelled, value ? 1 : 0);
        }

        public void ReleaseFirstRequest() => _firstRequestRelease.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/alerts/donations", request.RequestUri?.AbsolutePath);
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (_blockFirstRequest && requestNumber == 1)
            {
                await _firstRequestRelease.Task.WaitAsync(cancellationToken);
            }
            if (HangUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"data":[{"id":101,"username":"Viewer","message":"Test donation",
                    "amount":"50","currency":"RUB","created_at":"2026-08-12 10:00:00"}],
                    "links":{"next":null}}
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class RecordingDonationAlertsObsController : IDonationAlertsObsController
    {
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;
        public bool IsConfigured => true;
        public bool IsConnected => true;
        public bool IsAlertPlaying { get; private set; }
        public string ActiveAlertId { get; private set; } = string.Empty;
        public string SourceName => "DONATE";
        public List<string> RepeatedDonationIds { get; } = [];
        public List<string> SkippedDonationIds { get; } = [];
        public Exception? RepeatException { get; set; }
        public Exception? SkipException { get; set; }
        public TaskCompletionSource? RepeatGate { get; set; }
        public TaskCompletionSource? SkipGate { get; set; }
        public bool TryAutoConfigure() => true;
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task RepeatDonationAsync(
            DonationAlert donation,
            CancellationToken cancellationToken = default)
        {
            RepeatedDonationIds.Add(donation.Id);
            if (RepeatException is not null)
            {
                throw RepeatException;
            }
            if (RepeatGate is not null)
            {
                await RepeatGate.Task;
            }
            IsAlertPlaying = true;
            ActiveAlertId = donation.Id;
            if (long.TryParse(donation.Id, out var alertId))
            {
                PlaybackChanged?.Invoke(
                    this,
                    new DonationAlertsPlaybackEventArgs(DonationAlertsPlaybackAction.Started, alertId));
            }
        }

        public async Task SkipDonationAsync(
            DonationAlert donation,
            CancellationToken cancellationToken = default)
        {
            SkippedDonationIds.Add(donation.Id);
            if (SkipException is not null)
            {
                throw SkipException;
            }
            if (SkipGate is not null)
            {
                await SkipGate.Task;
            }
            IsAlertPlaying = false;
            ActiveAlertId = string.Empty;
            if (long.TryParse(donation.Id, out var alertId))
            {
                PlaybackChanged?.Invoke(
                    this,
                    new DonationAlertsPlaybackEventArgs(DonationAlertsPlaybackAction.Skipped, alertId));
            }
        }

        public void RaiseStarted(DonationAlert donation)
        {
            IsAlertPlaying = true;
            ActiveAlertId = donation.Id;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(
                    DonationAlertsPlaybackAction.Started,
                    long.Parse(donation.Id)));
        }

        public void RaiseSkipped(DonationAlert donation)
        {
            IsAlertPlaying = false;
            ActiveAlertId = string.Empty;
            PlaybackChanged?.Invoke(
                this,
                new DonationAlertsPlaybackEventArgs(
                    DonationAlertsPlaybackAction.Skipped,
                    long.Parse(donation.Id)));
        }
    }

    private sealed class UnavailableDonationAlertsObsController : IDonationAlertsObsController
    {
        public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged
        {
            add { }
            remove { }
        }
        public bool IsConfigured => false;
        public bool IsConnected => false;
        public bool IsAlertPlaying => false;
        public string ActiveAlertId => string.Empty;
        public string SourceName => string.Empty;
        public List<string> RepeatedDonationIds { get; } = [];
        public bool TryAutoConfigure() => false;
        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Direct control is unavailable."));
        public Task RepeatDonationAsync(
            DonationAlert donation,
            CancellationToken cancellationToken = default)
        {
            RepeatedDonationIds.Add(donation.Id);
            return Task.CompletedTask;
        }
        public Task SkipDonationAsync(
            DonationAlert donation,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static ChannelSearchResultViewModel CreateSearchResult(string login) =>
        new(
            new ChannelSearchResult(
                login + "-id",
                login,
                login,
                $"https://example.test/{login}.png",
                "Just Chatting",
                string.Empty,
                false,
                DateTimeOffset.MinValue,
                0),
            null);

    private sealed class RecordingChatClient : IWitherChatClient
    {
        private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<ChatMessageEventArgs>? MessageReceived;

        public event EventHandler<ChatConnectionStatusEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public string CurrentChannel { get; private set; } = string.Empty;
        public IReadOnlyCollection<string> Channels => _channels.ToArray();
        public bool IsConnected => _channels.Count > 0;
        public bool HasMessageReceiver => MessageReceived is not null;

        public void Publish(ChatMessage message) =>
            MessageReceived?.Invoke(this, new ChatMessageEventArgs(message));

        public Task ConnectAsync(string channel, CancellationToken cancellationToken = default) =>
            JoinChannelAsync(channel, cancellationToken);

        public Task JoinChannelAsync(string channel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentChannel = channel;
            _channels.Add(channel);
            return Task.CompletedTask;
        }

        public Task PartChannelAsync(string channel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _channels.Remove(channel);
            CurrentChannel = _channels.FirstOrDefault() ?? string.Empty;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _channels.Clear();
            CurrentChannel = string.Empty;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryTokenStore : ISecureTokenStore
    {
        private byte[]? _value;
        public bool IsPersistent => false;
        public byte[]? Load() => _value?.ToArray();
        public bool TrySave(byte[] data)
        {
            _value = data.ToArray();
            return true;
        }
        public void Clear() => _value = null;
    }

    private sealed class StubTwitchHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/helix/search/channels",
                    StringComparison.Ordinal) == true)
            {
                var isPartialSearch = request.RequestUri.Query.Contains(
                    "query=bra",
                    StringComparison.OrdinalIgnoreCase);
                var searchJson = isPartialSearch
                    ? """
                      {"data":[
                        {"id":"1","broadcaster_login":"bratishkinoff","display_name":"Bratishkinoff","thumbnail_url":"https://example.test/bratish.png","game_name":"MOLE","title":"","is_live":false,"started_at":""},
                        {"id":"2","broadcaster_login":"braeden","display_name":"Braeden","thumbnail_url":"https://example.test/braeden.png","game_name":"IRL","title":"Live","is_live":true,"started_at":"2026-07-28T10:00:00Z"},
                        {"id":"3","broadcaster_login":"brawks","display_name":"Brawks","thumbnail_url":"https://example.test/brawks.png","game_name":"VALORANT","title":"","is_live":false,"started_at":""},
                        {"id":"4","broadcaster_login":"brawlstars","display_name":"BrawlStars","thumbnail_url":"https://example.test/brawlstars.png","game_name":"Brawl Stars","title":"","is_live":false,"started_at":""},
                        {"id":"5","broadcaster_login":"brax","display_name":"Brax","thumbnail_url":"https://example.test/brax.png","game_name":"Counter-Strike","title":"","is_live":false,"started_at":""},
                        {"id":"6","broadcaster_login":"brawlhalla","display_name":"Brawlhalla","thumbnail_url":"https://example.test/brawlhalla.png","game_name":"Brawlhalla","title":"","is_live":false,"started_at":""}
                      ],"pagination":{}}
                      """
                    : """{"data":{"user":null}}""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(searchJson, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/helix/streams",
                    StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"user_id":"2","viewer_count":314}],"pagination":{}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/helix/users",
                    StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"viewer-id","login":"viewer","display_name":"Viewer","profile_image_url":"https://static-cdn.jtvnw.net/user-default-pictures-uv/viewer-profile.png"}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var found = body.Contains("wither_101", StringComparison.OrdinalIgnoreCase);
            var json = found
                ? """
                  {"data":{"user":{"id":"1333126195","login":"wither_101","displayName":"WitheR_101","profileImageURL":"https://static-cdn.jtvnw.net/user-default-pictures-uv/ead5c8b2-a4c9-4724-b1dd-9f3d8ecfcea7-profile_image-70x70.png","stream":{"viewersCount":42,"game":{"name":"Minecraft"}}}}}
                  """
                : """{"data":{"user":null}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SequencedStreamHandler(params TwitchStreamStatus[] statuses) : HttpMessageHandler
    {
        private int _requestIndex;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.EndsWith("/helix/streams", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var index = Math.Min(Interlocked.Increment(ref _requestIndex) - 1, statuses.Length - 1);
            var status = statuses[index];
            var json = status.IsLive
                ? $"{{\"data\":[{{\"user_id\":\"channel-id\",\"viewer_count\":{status.ViewerCount}}}],\"pagination\":{{}}}}"
                : """{"data":[],"pagination":{}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MalformedStreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
            });
    }

    private sealed class EmptyPublicChannelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            });
    }

    private sealed class MalformedPinnedMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var payload = request.RequestUri?.AbsolutePath.EndsWith("/helix/users", StringComparison.Ordinal) == true
                ? """{"data":[{"id":"channel-id","login":"wither_101","display_name":"WitheR_101"}]}"""
                : "{not-json";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestPlatformDescriptor : IPlatformDescriptor
    {
        public string Id => "headless";
        public string DisplayName => "Headless";
        public IReadOnlyList<string> RuntimeIdentifiers => [];
        public bool SupportsSystemTray => false;
    }
}
