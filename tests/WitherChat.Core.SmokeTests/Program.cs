using WitherChat.Core.Models;
using WitherChat.Core.Services;
using System.Text.Json;

var failures = new List<string>();

Check(TwitchChatApiClient.ParseOptionalTimestamp(null) is null,
    "A permanent Twitch ban with null expires_at must deserialize without an expiry.");
Check(TwitchChatApiClient.ParseOptionalTimestamp(string.Empty) is null,
    "A permanent Twitch ban with an empty expires_at must deserialize without an expiry.");
Check(TwitchChatApiClient.ParseOptionalTimestamp("2026-07-28T10:15:30Z") is not null,
    "A temporary Twitch ban expiry must remain parseable.");

Check(
    TwitchIrcClient.TryParseChatMessage(
        "@badges=moderator/1,subscriber/12;color=#00FF7F;custom-reward-id=reward42;display-name=Tester;id=abc123;user-id=42;reply-parent-display-name=Parent;reply-parent-msg-body=hello\\sworld;tmi-sent-ts=1710000000000 :tester!tester@tester.tmi.twitch.tv PRIVMSG #witherchat :hello chat",
        "fallback",
        out var message),
    "A normal PRIVMSG must parse.");
Check(message.Id == "abc123", "Message id must be preserved.");
Check(message.Channel == "witherchat", "Channel must be parsed.");
Check(message.DisplayName == "Tester", "Display name must be parsed.");
Check(message.UserId == "42", "User id required by moderation must be parsed.");
Check(message.Text == "hello chat", "Message text must be parsed.");
Check(message.Badges.Select(badge => badge.SetId).SequenceEqual(["moderator", "subscriber"]),
    "Badge names must be parsed in order.");
Check(message.Badges[1].VersionId == "12", "Badge versions must be preserved.");
Check(message.ReplyParentText == "hello world", "IRC tag escaping must be decoded.");
Check(message.IsChannelPointRedemption && message.CustomRewardId == "reward42",
    "Channel point redemption metadata must be preserved.");

using (var redemptionDocument = JsonDocument.Parse("""
{
  "id":"redeem-1","broadcaster_user_id":"100","broadcaster_user_login":"witherchat",
  "user_id":"42","user_login":"tester","user_name":"Tester","user_input":"hello points",
  "redeemed_at":"2026-07-18T20:00:00Z",
  "reward":{"id":"reward-1","title":"Highlight","cost":150,"prompt":"Say something"}
}
"""))
{
    var redemption = TwitchEventSubClient.ParseChannelPoints(
        redemptionDocument.RootElement, "channel.channel_points_custom_reward_redemption.add");
    Check(redemption is { IsChannelPointRedemption: true, RewardTitle: "Highlight", RewardCost: 150 } &&
          redemption.Text == "hello points" && redemption.Channel == "witherchat",
        "EventSub Channel Points details must be parsed without losing reward metadata.");
}

using (var autoModDocument = JsonDocument.Parse("""
{
  "message_id":"held-1","broadcaster_user_id":"100","broadcaster_user_login":"witherchat",
  "user_id":"42","user_login":"tester","user_name":"Tester","held_at":"2026-07-18T20:00:00Z",
  "message":{"text":"held message"},"reason":{"category":"aggression","level":3}
}
"""))
{
    var held = TwitchEventSubClient.ParseAutoMod(autoModDocument.RootElement);
    Check(held is { MessageId: "held-1", MessageText: "held message", Category: "aggression", Level: 3 },
        "EventSub AutoMod holds must preserve the moderation context.");
}

using (var sharedChatDocument = JsonDocument.Parse("""
{
  "message_id":"shared-1","broadcaster_user_id":"100","broadcaster_user_login":"witherchat",
  "chatter_user_id":"42","chatter_user_login":"tester","chatter_user_name":"Tester",
  "source_broadcaster_user_id":"200","source_broadcaster_user_login":"partner",
  "source_broadcaster_user_name":"Partner","color":"#00FF7F","message_type":"text",
  "source_badges":[{"set_id":"moderator","id":"1"}],
  "message":{"text":"shared hello","fragments":[{"type":"text","text":"shared hello"}]}
}
"""))
{
    var shared = TwitchEventSubClient.ParseChatMessage(
        sharedChatDocument.RootElement, "2026-07-18T20:00:00Z");
    Check(shared is { IsSharedChatMessage: true, SourceChannelLogin: "partner" } &&
          shared.Text == "shared hello" && shared.Badges.Single().SetId == "moderator",
        "EventSub Shared Chat messages must preserve source-channel and badge metadata.");
}

var emoteParts = TwitchEmoteParser.Parse("Kappa hello Kappa", "25:0-4,12-16");
Check(emoteParts.Count == 3, "Twitch emote ranges must create three message parts.");
Check(emoteParts[0].Kind == ChatMessagePartKind.Emote && emoteParts[0].Text == "Kappa",
    "The first Twitch emote range was not preserved.");
Check(emoteParts[1].Kind == ChatMessagePartKind.Text && emoteParts[1].Text == " hello ",
    "Text between Twitch emotes was not preserved.");
Check(emoteParts[2].Kind == ChatMessagePartKind.Emote && emoteParts[2].EmoteId == "25",
    "The second Twitch emote range was not preserved.");
Check(TwitchEmoteParser.Parse("plain text", "broken").Single().Text == "plain text",
    "Malformed emote metadata must fall back to the original text.");

var unicodeEmoteParts = TwitchEmoteParser.Parse("😀 Kappa", "25:2-6");
Check(unicodeEmoteParts.Count == 2 && unicodeEmoteParts[0].Text == "😀 " &&
      unicodeEmoteParts[1].Kind == ChatMessagePartKind.Emote && unicodeEmoteParts[1].Text == "Kappa",
    "Twitch emote positions after supplementary Unicode characters must map to UTF-16 correctly.");

var thirdPartyCatalog = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal)
{
    ["KEKW"] = new ThirdPartyEmote("1", "KEKW", new Uri("https://cdn.7tv.app/emote/1/2x.png"), "7TV")
};
var thirdPartyParts = ThirdPartyEmoteTokenizer.Tokenize(
    [ChatMessagePart.PlainText("hello KEKW! https://example.test/KEKW")],
    thirdPartyCatalog);
Check(thirdPartyParts.Count == 3, "Third-party emote tokenization produced an unexpected part count.");
Check(thirdPartyParts[1].Kind == ChatMessagePartKind.Emote && thirdPartyParts[1].Text == "KEKW",
    "A third-party emote next to punctuation was not recognized.");
Check(thirdPartyParts[2].Text == "! https://example.test/KEKW",
    "Punctuation and URL text must be preserved without false emote matches.");

using (var sevenTvDocument = JsonDocument.Parse("""
[
  {
    "id":"overlay-id","name":"OVERLAY","flags":0,"aliases":["OVERLAY_ALIAS"],
    "data":{
      "id":"overlay-id","flags":256,
      "host":{"url":"//cdn.7tv.app/emote/overlay-id","files":[
        {"name":"2x.png","width":64,"height":64},
        {"name":"2x.gif","width":64,"height":64}
      ]}
    }
  }
]
"""))
{
    var sevenTv = ThirdPartyEmoteCatalogService.ParseSevenTvArray(sevenTvDocument.RootElement);
    Check(sevenTv.Count == 2 &&
          sevenTv.All(emote => emote.IsZeroWidth && emote.ImageUri.AbsolutePath.EndsWith("2x.gif", StringComparison.Ordinal)) &&
          sevenTv.Any(emote => emote.Code == "OVERLAY_ALIAS") &&
          sevenTv.All(emote => emote.SourceWidth == 64 && emote.SourceHeight == 64),
        "7TV animated zero-width emotes and aliases must preserve their overlay metadata.");
    var overlayCatalog = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal)
    {
        ["BASE"] = new ThirdPartyEmote(
            "base",
            "BASE",
            new Uri("https://cdn.7tv.app/emote/base/2x.png"),
            "7TV"),
        ["OVERLAY"] = sevenTv.First(emote => emote.Code == "OVERLAY")
    };
    var composite = ThirdPartyEmoteTokenizer.Tokenize(
        [ChatMessagePart.PlainText("BASE OVERLAY")],
        overlayCatalog);
    Check(composite.Count == 3 && composite[0].Text == "BASE" &&
          composite[1].Kind == ChatMessagePartKind.Text &&
          composite[2].IsZeroWidth,
        "The chat presentation pipeline must retain a zero-width emote after its base emote.");
}

Check(
    TwitchIrcClient.TryParseChatMessage(
        "@display-name=ActionUser;tmi-sent-ts=1710000000000 :actionuser!u@h PRIVMSG #witherchat :\u0001ACTION waves\u0001",
        "fallback",
        out var actionMessage),
    "An ACTION message must parse.");
Check(actionMessage.IsAction, "ACTION flag must be set.");
Check(actionMessage.Text == "waves", "ACTION framing must be removed.");

Check(
    !TwitchIrcClient.TryParseChatMessage("PING :tmi.twitch.tv", "fallback", out _),
    "Non-message IRC lines must be ignored.");

var settings = new WitherChatSettings
{
    Channel = "  #Mixed_Channel ",
    SavedChannels = ["#Mixed_Channel", "Second", "SECOND", "invalid channel", "third", "fourth"],
    Theme = "Unknown",
    Language = "de",
    MessageLimit = 100_000,
    ViewerCountRefreshIntervalSeconds = 1,
    ChatFontSize = 2,
    HasCompletedOnboarding = true
};
settings.Normalize();
Check(settings.Channel == "mixed_channel", "Channel normalization failed.");
Check(settings.SavedChannels.SequenceEqual(["mixed_channel", "second", "third"]),
    "Saved channels must be normalized, de-duplicated and limited to three.");
Check(settings.Theme == "Dark", "Unknown theme must fall back to Dark.");
Check(settings.Language == "ru", "Unknown language must fall back to Russian.");
Check(settings.UiFontFamily == "SegoeUIVariable",
    "Unknown UI fonts must preserve the 0.3.3 Segoe UI Variable default.");
Check(settings.MessageLimit == 10_000, "Message limit must be clamped.");
Check(
    settings.ViewerCountRefreshIntervalSeconds == WitherChatSettings.MinimumViewerCountRefreshIntervalSeconds,
    "Viewer refresh interval must be clamped.");
Check(Math.Abs(settings.ChatFontSize - 11) < double.Epsilon, "Font size must be clamped.");
Check(settings.HasCompletedOnboarding, "The completed onboarding state must survive settings normalization.");

settings.Channel = null!;
settings.Theme = null!;
settings.Language = null!;
settings.Normalize();
Check(settings.Channel.Length == 0 && settings.Theme == "Dark" && settings.Language == "ru",
    "Semantically invalid but valid JSON settings must normalize without crashing.");

var redirectUri = new Uri(TwitchApplication.RedirectUri);
Check(TwitchAuthService.IsTrustedLoopbackRedirectUri(redirectUri),
    "The configured OAuth callback must use a trusted loopback URI.");
Check(!TwitchAuthService.IsTrustedLoopbackRedirectUri(new Uri("https://localhost:17654/")) &&
      !TwitchAuthService.IsTrustedLoopbackRedirectUri(new Uri("http://example.test:17654/")) &&
      !TwitchAuthService.IsTrustedLoopbackRedirectUri(new Uri("http://localhost:17654/callback")),
    "Non-loopback or structurally unexpected OAuth callbacks must be rejected.");
var authorizeUri = TwitchAuthService.BuildAuthorizeUri(redirectUri, "test-state");
Check(authorizeUri.Scheme == Uri.UriSchemeHttps && authorizeUri.Host == "id.twitch.tv",
    "Browser OAuth must use Twitch's HTTPS authorization endpoint.");
Check(authorizeUri.Query.Contains("response_type=token", StringComparison.Ordinal) &&
      authorizeUri.Query.Contains("state=test-state", StringComparison.Ordinal) &&
      authorizeUri.Query.Contains(Uri.EscapeDataString(TwitchApplication.RedirectUri), StringComparison.Ordinal),
    "Browser OAuth must carry the implicit grant, state, and exact callback URI.");
Check(TwitchApplication.ChatScopes.Contains("moderator:manage:banned_users", StringComparer.Ordinal) &&
      TwitchApplication.ChatScopes.Contains("moderator:manage:chat_messages", StringComparer.Ordinal),
    "Browser OAuth must request the moderation scopes used by WitherChat 0.3.0.");

var logDirectory = Path.Combine(Path.GetTempPath(), "WitherChat-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var logWriter = new ChatLogWriter(logDirectory);
    logWriter.Enqueue(message with
    {
        DisplayName = "Tester\r\nInjected",
        Text = "hello\r\nchat"
    });
    await logWriter.DisposeAsync();
    await logWriter.DisposeAsync();
    var logFile = Directory.EnumerateFiles(logDirectory, "chat.txt", SearchOption.AllDirectories).SingleOrDefault();
    var logLines = logFile is null ? [] : await File.ReadAllLinesAsync(logFile);
    Check(logLines.Length == 1 && logLines[0].Contains("Tester  Injected: hello  chat", StringComparison.Ordinal),
        "The asynchronous chat log must flush one sanitized line during shutdown.");
    var jsonLogFile = Directory.EnumerateFiles(logDirectory, "chat.jsonl", SearchOption.AllDirectories).SingleOrDefault();
    var jsonLogLines = jsonLogFile is null ? [] : await File.ReadAllLinesAsync(jsonLogFile);
    Check(jsonLogLines.Length == 1 &&
          jsonLogLines[0].Contains("reward42", StringComparison.Ordinal) &&
          jsonLogLines[0].Contains("moderator", StringComparison.Ordinal),
        "The structured chat log must preserve Channel Points and badge metadata.");
    var metadataFile = Directory.EnumerateFiles(logDirectory, "metadata.json", SearchOption.AllDirectories)
        .SingleOrDefault();
    Check(metadataFile is not null &&
          (await File.ReadAllTextAsync(metadataFile)).Contains("\"messageCount\": 1", StringComparison.Ordinal),
        "The WPF-compatible daily chat log metadata was not written.");
}
finally
{
    if (Directory.Exists(logDirectory))
    {
        Directory.Delete(logDirectory, recursive: true);
    }
}

var moderationDirectory = Path.Combine(
    Path.GetTempPath(),
    "WitherChat-moderation-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var moderationPaths = new AppDataPaths(moderationDirectory);
    await using (var cache = new ModerationCacheStore(moderationPaths))
    {
        cache.ScheduleSave(
            "100",
            [new BannedUser("42", "tester", "Tester", DateTimeOffset.UtcNow, null, "reason")],
            [new UnbanRequest(
                "request-1",
                "100",
                "42",
                "tester",
                "Tester",
                "please",
                DateTimeOffset.UtcNow,
                UnbanRequestStatus.Pending,
                null,
                string.Empty)]);
    }
    await using (var restoredCache = new ModerationCacheStore(moderationPaths))
    {
        var restored = restoredCache.Restore("100");
        Check(restored.BannedUsers.Count == 1 && restored.UnbanRequests.Count == 1,
            "Moderation cache must survive a restart.");
    }
}
finally
{
    if (Directory.Exists(moderationDirectory))
    {
        Directory.Delete(moderationDirectory, recursive: true);
    }
}

var sessionDirectory = Path.Combine(Path.GetTempPath(), "WitherChat-session-smoke-" + Guid.NewGuid().ToString("N"));
Console.WriteLine("Checking protected session storage...");
TwitchAuthSessionStore? smokeSessionStore = null;
try
{
    var sessionPaths = new AppDataPaths(sessionDirectory);
    smokeSessionStore = new TwitchAuthSessionStore(sessionPaths);
    var session = new TwitchAuthSession(
        "plain-access-token-must-not-be-stored",
        "plain-refresh-token-must-not-be-stored",
        TwitchApplication.ClientId,
        "42",
        "tester",
        ["chat:read"],
        DateTimeOffset.UtcNow.AddHours(1),
        DateTimeOffset.UtcNow);
    smokeSessionStore.Save(session);
    if (OperatingSystem.IsWindows())
    {
        var storedBytes = await File.ReadAllBytesAsync(sessionPaths.TokenFile);
        var storedText = System.Text.Encoding.UTF8.GetString(storedBytes);
        Check(!storedText.Contains(session.AccessToken, StringComparison.Ordinal),
            "The saved Twitch session must not expose the access token as plaintext.");
    }
    else
    {
        Check(!File.Exists(sessionPaths.TokenFile),
            "Keychain or secret-service sessions must not create a plaintext token file.");
    }

    var loadedSession = smokeSessionStore.Load();
    Check(loadedSession?.AccessToken == session.AccessToken && loadedSession.RefreshToken == session.RefreshToken,
        "The protected Twitch session did not round-trip.");
}
finally
{
    smokeSessionStore?.Clear();
    if (Directory.Exists(sessionDirectory))
    {
        Directory.Delete(sessionDirectory, recursive: true);
    }
}

if (OperatingSystem.IsWindows())
{
    Console.WriteLine("Checking the OBS overlay endpoint...");
    try
    {
        var keepOverlayPreviewOpen = args.Contains("--overlay-preview", StringComparer.Ordinal);
        var overlayPort = 17656;
        if (!keepOverlayPreviewOpen)
        {
            var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            portProbe.Start();
            overlayPort = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
        }
        await using var overlay = new ObsOverlayServer();
        await overlay.ConfigureAsync(
            true,
            new ObsOverlayOptions(
                overlayPort, 12, 22, true, true, true, 0, true, true, true, 0, "flex-start", "TornBlack"));
        using var overlayHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var overlayHtml = await overlayHttp.GetStringAsync(overlay.Url);
        Check(overlayHtml.Contains("/overlay/events", StringComparison.Ordinal) &&
              overlayHtml.Contains("EventSource", StringComparison.Ordinal) &&
              overlayHtml.Contains("emote-stack", StringComparison.Ordinal) &&
              overlayHtml.Contains("p.zeroWidth", StringComparison.Ordinal) &&
              overlayHtml.Contains("box-sizing:border-box;min-width:0;max-width:100%", StringComparison.Ordinal) &&
              overlayHtml.Contains("overflow-wrap:anywhere;word-break:break-word", StringComparison.Ordinal) &&
              overlayHtml.Contains("width:100vw;max-width:100vw", StringComparison.Ordinal) &&
              overlayHtml.Contains("border-image-slice:55 100 fill", StringComparison.Ordinal) &&
              overlayHtml.Contains("--message-border-width", StringComparison.Ordinal) &&
              overlayHtml.Contains("--message-left-fill-size", StringComparison.Ordinal) &&
              overlayHtml.Contains("/overlay/assets/inter-variable.ttf", StringComparison.Ordinal) &&
              overlayHtml.Contains("/overlay/assets/message-torn-black.png", StringComparison.Ordinal),
            "The OBS Browser Source endpoint did not return the live chat overlay.");
        var tornBackground = await overlayHttp.GetByteArrayAsync(
            $"http://localhost:{overlayPort}/overlay/assets/message-torn-black.png");
        Check(tornBackground.Length > 1024 &&
              tornBackground.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "The torn-black OBS message asset is unavailable.");
        var interFont = await overlayHttp.GetByteArrayAsync(
            $"http://localhost:{overlayPort}/overlay/assets/inter-variable.ttf");
        Check(interFont.Length > 100_000,
            "The embedded Inter font is unavailable to the OBS Browser Source.");
        var overlaySettings = await overlayHttp.GetStringAsync(
            $"http://localhost:{overlayPort}/overlay/settings");
        Check(overlaySettings.Contains("\"messageTheme\":\"TornBlack\"", StringComparison.Ordinal) &&
              overlaySettings.Contains("\"maximumMessages\":12", StringComparison.Ordinal),
            "The OBS Browser Source settings endpoint is incomplete.");
        var overlayEmoteMessage = message with
        {
            Id = message.Id + "-zero-width",
            Parts =
            [
                new ChatMessagePart
                {
                    Kind = ChatMessagePartKind.Emote,
                    Text = "BASE",
                    ImageUri = new Uri("https://cdn.7tv.app/emote/base/2x.png"),
                    Provider = ThirdPartyEmoteProviders.SevenTv,
                    EmoteId = "base"
                },
                ChatMessagePart.PlainText(" "),
                new ChatMessagePart
                {
                    Kind = ChatMessagePartKind.Emote,
                    Text = "OVERLAY",
                    ImageUri = new Uri("https://cdn.7tv.app/emote/overlay/2x.gif"),
                    Provider = ThirdPartyEmoteProviders.SevenTv,
                    EmoteId = "overlay",
                    IsZeroWidth = true
                }
            ]
        };
        overlay.Publish(overlayEmoteMessage);
        using (var eventsResponse = await overlayHttp.GetAsync(
                   $"http://localhost:{overlayPort}/overlay/events",
                   HttpCompletionOption.ResponseHeadersRead))
        await using (var eventsStream = await eventsResponse.Content.ReadAsStreamAsync())
        using (var eventsReader = new StreamReader(eventsStream))
        {
            string? eventLine;
            do
            {
                eventLine = await eventsReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            } while (eventLine is not null && !eventLine.StartsWith("data: ", StringComparison.Ordinal));
            Check(eventLine?.Contains(overlayEmoteMessage.Id, StringComparison.Ordinal) == true &&
                  eventLine.Contains("\"zeroWidth\":true", StringComparison.Ordinal),
                "A newly opened OBS Browser Source must receive composited 7TV message history.");
        }
        if (keepOverlayPreviewOpen)
        {
            var longText =
                "Telegram: https://example.com/this/is/a/very/long/address/that/must/wrap/inside/the/obs/browser/source?campaign=witherchat " +
                new string('X', 320);
            overlay.Publish(message with
            {
                Id = message.Id + "-long-preview",
                DisplayName = "LongMessageTester",
                Text = longText,
                Parts = [ChatMessagePart.PlainText(longText)]
            });
            Console.WriteLine($"OBS overlay preview: http://localhost:{overlayPort}/overlay/chat");
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
        await overlay.ConfigureAsync(false, new ObsOverlayOptions(
            overlayPort, 12, 22, true, true, true, 0, true, true, true, 0, "flex-start", "TornBlack"));
    }
    catch (System.Net.HttpListenerException exception) when (exception.ErrorCode is 5 or 6)
    {
        Console.WriteLine($"OBS overlay smoke test skipped: {exception.Message}");
    }
}

if (args.Contains("--live", StringComparer.Ordinal))
{
    await using var client = new TwitchIrcClient();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    await client.ConnectAsync("twitch", cancellation.Token);
    Check(client.IsConnected, "Live Twitch IRC connection did not reach Connected state.");
    await client.DisconnectAsync();
}

if (args.Contains("--live-eventsub", StringComparer.Ordinal))
{
    var paths = new AppDataPaths();
    var sessionStore = new TwitchAuthSessionStore(paths);
    var storedSession = sessionStore.Load()
        ?? throw new InvalidOperationException("A saved Twitch session is required for the live EventSub smoke test.");
    using var authService = new TwitchAuthService();
    authService.Configure(storedSession.ClientId, TwitchApplication.RedirectUri);
    var liveSession = await authService.ValidateAsync(storedSession);
    using var apiClient = new TwitchChatApiClient(liveSession.ClientId);
    await using var eventSub = new TwitchEventSubClient(apiClient);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    eventSub.SubscriptionsReady += (_, _) => ready.TrySetResult();
    await eventSub.ConfigureAsync(liveSession, ["twitch"]);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
    Check(eventSub.HasEventSubChat("twitch"),
        "The live EventSub socket connected but channel.chat.message was not accepted.");
}

if (args.Contains("--live-search", StringComparer.Ordinal))
{
    var paths = new AppDataPaths();
    var sessionStore = new TwitchAuthSessionStore(paths);
    var storedSession = sessionStore.Load()
        ?? throw new InvalidOperationException("A saved Twitch session is required for the live search smoke test.");
    using var authService = new TwitchAuthService();
    authService.Configure(storedSession.ClientId, TwitchApplication.RedirectUri);
    var liveSession = await authService.ValidateAsync(storedSession);
    using var apiClient = new TwitchChatApiClient(liveSession.ClientId);
    var liveResults = await apiClient.SearchChannelsAsync(liveSession, "bra");
    Check(liveResults.Count > 0, "Live Twitch partial channel search returned no suggestions for 'bra'.");
    Console.WriteLine("Live search suggestions: " +
                      string.Join(", ", liveResults.Select(result => result.BroadcasterLogin)));
}

if (args.Contains("--live-api", StringComparer.Ordinal))
{
    var paths = new AppDataPaths();
    var sessionStore = new TwitchAuthSessionStore(paths);
    var storedSession = sessionStore.Load()
        ?? throw new InvalidOperationException("A saved Twitch session is required for the live API check.");
    using var authService = new TwitchAuthService();
    authService.Configure(storedSession.ClientId, TwitchApplication.RedirectUri);
    var liveSession = await authService.ValidateAsync(storedSession);
    using var apiClient = new TwitchChatApiClient(liveSession.ClientId);
    var profile = await apiClient.GetUserProfileAsync(liveSession, liveSession.Login);
    var publicProfile = await apiClient.GetPublicChannelAsync(liveSession.Login);
    var stream = await apiClient.GetStreamStatusAsync(liveSession, liveSession.Login);
    var search = await apiClient.SearchChannelsAsync(liveSession, liveSession.Login);
    var badges = await apiClient.GetBadgeCatalogAsync(liveSession, liveSession.Login);
    var moderation = await apiClient.GetModerationAccessAsync(liveSession, liveSession.Login);
    var pinned = await apiClient.GetPinnedChatMessageAsync(liveSession, liveSession.Login);
    var banned = await apiClient.GetBannedUsersAsync(liveSession, liveSession.Login);
    var appeals = await apiClient.GetUnbanRequestsAsync(liveSession, liveSession.Login);
    using var emoteCatalog = new ThirdPartyEmoteCatalogService();
    var emotes = await emoteCatalog.LoadAsync(profile.Id);

    Check(profile.Id == liveSession.UserId && profile.Login.Length > 0,
        "Live Twitch profile lookup returned another account.");
    Check(publicProfile?.Id == profile.Id,
        "Public Twitch channel lookup disagrees with the authenticated profile.");
    Check(search.Any(result => string.Equals(result.Id, profile.Id, StringComparison.Ordinal)),
        "Live exact channel search did not include the authenticated channel.");
    Check(moderation.IsBroadcaster && moderation.CanModerate,
        "The authenticated account was not recognized as owner of its channel.");
    Check(emotes.Values.Any(emote => emote.Provider == "BTTV") &&
          emotes.Values.Any(emote => emote.Provider == "7TV"),
        "Live BTTV/7TV catalogs did not both return emotes.");
    Console.WriteLine(
        $"Live API check: @{profile.Login}, live={stream.IsLive}, viewers={stream.ViewerCount}, " +
        $"badges={badges.Count}, pinned={(pinned is null ? "none" : "present")}, " +
        $"bans={banned.Users.Count}, appeals={appeals.Requests.Count}, emotes={emotes.Count}.");
}

var liveModerationArgument = args.FirstOrDefault(argument =>
    argument.StartsWith("--live-moderation=", StringComparison.Ordinal));
if (liveModerationArgument is not null)
{
    var channel = liveModerationArgument["--live-moderation=".Length..].Trim().TrimStart('@');
    if (channel.Length == 0)
    {
        throw new ArgumentException("The live moderation check requires a channel login.");
    }

    var paths = new AppDataPaths();
    var sessionStore = new TwitchAuthSessionStore(paths);
    var storedSession = sessionStore.Load()
        ?? throw new InvalidOperationException("A saved Twitch session is required for the live moderation check.");
    using var authService = new TwitchAuthService();
    authService.Configure(storedSession.ClientId, TwitchApplication.RedirectUri);
    var liveSession = await authService.ValidateAsync(storedSession);
    using var apiClient = new TwitchChatApiClient(liveSession.ClientId);
    var access = await apiClient.GetModerationAccessAsync(liveSession, channel);
    Check(access.CanModerate, $"The saved account does not have moderation access to @{channel}.");
    if (access.CanModerate)
    {
        var banned = await apiClient.GetBannedUsersAsync(liveSession, channel);
        var appeals = await apiClient.GetUnbanRequestsAsync(liveSession, channel);
        Console.WriteLine(
            $"Live moderation read check: @{channel}, bans={banned.Users.Count}, pending appeals={appeals.Requests.Count}.");
    }
}

if (failures.Count == 0)
{
    Console.WriteLine("WitherChat core smoke tests passed.");
    return 0;
}

foreach (var failure in failures)
{
    Console.Error.WriteLine("FAIL: " + failure);
}

return 1;

void Check(bool condition, string failure)
{
    if (!condition)
    {
        failures.Add(failure);
    }
}
