using System.Collections.Specialized;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Platforms;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;

var failures = new List<string>();
var platform = PlatformDescriptorFactory.CreateCurrent();
Check(
    platform.RuntimeIdentifiers.Count == 2 &&
    platform.RuntimeIdentifiers.All(runtime => runtime.EndsWith("-x64", StringComparison.Ordinal) ||
                                               runtime.EndsWith("-arm64", StringComparison.Ordinal)),
    "The current platform must expose x64 and ARM64 release targets.");
var collection = new LiveMessageCollection<int>();
collection.AppendBatch([1, 2, 3], 5);
Check(collection.SequenceEqual([1, 2, 3]), "The initial message batch was not appended in order.");

var trimNotifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
collection.CollectionChanged += (_, changed) => trimNotifications.Add(changed.Action);
collection.AppendBatch([4, 5, 6, 7], 5);
Check(collection.SequenceEqual([3, 4, 5, 6, 7]),
    "Range trimming must preserve the newest messages in order.");
Check(trimNotifications.SequenceEqual([System.Collections.Specialized.NotifyCollectionChangedAction.Reset]),
    "Trimming and appending must be published as one atomic visual update.");

collection.AppendBatch([8, 9, 10, 11, 12], 3);
Check(collection.SequenceEqual([10, 11, 12]),
    "An incoming batch larger than the limit must keep only its newest items.");

collection.TrimToMaximum(2);
Check(collection.SequenceEqual([11, 12]), "Explicit range trimming produced the wrong items.");
collection.RemoveFirst(1);
Check(collection.SequenceEqual([12]), "Removing the visible prefix produced the wrong items.");
collection.RemoveFirst(10);
Check(collection.Count == 0, "Removing more visible items than available must leave an empty collection.");

var legacyBuffer = new LiveMessageCollection<int>();
legacyBuffer.AppendBatch(Enumerable.Range(1, 650).ToArray(), int.MaxValue);
NotifyCollectionChangedAction? legacyTrimAction = null;
legacyBuffer.CollectionChanged += (_, eventArgs) => legacyTrimAction = eventArgs.Action;
Check(
    LiveChatBufferPolicy.GetTrimTrigger(500) == 650,
    "The 0.3.0 chat buffer must retain its 150-message trimming headroom.");
legacyBuffer.RemoveOldestRange(legacyBuffer.Count - 500);
Check(
    legacyBuffer.Count == 500 && legacyBuffer[0] == 151 && legacyBuffer[^1] == 650,
    "The 0.3.0 headroom trim must keep the newest messages without reordering them.");
Check(
    legacyTrimAction == NotifyCollectionChangedAction.Remove,
    "Trimming the visible chat must preserve virtualized rows instead of resetting the whole list.");
legacyBuffer.AppendRangeAndTrim([651, 652], 500);
Check(
    legacyBuffer.Count == 500 && legacyBuffer[^1] == 652 &&
    !legacyBuffer.IsTrimming && !legacyBuffer.IsBatchUpdating,
    "Flushing deferred messages must atomically restore the configured chat limit.");

var texts = new UiText();
Check(texts.ApiDisconnected == "API \u043e\u0442\u043a\u043b\u044e\u0447\u0451\u043d" &&
      texts.ChatDisconnected == "\u0427\u0430\u0442 \u043e\u0442\u043a\u043b\u044e\u0447\u0451\u043d" &&
      texts.Offline == "\u041d\u0435 \u0432 \u044d\u0444\u0438\u0440\u0435" &&
      texts.ConnectTwitch == "\u041f\u043e\u0434\u043a\u043b\u044e\u0447\u0438\u0442\u044c Twitch",
    "The disconnected header and sign-in labels must match the 0.3.3 interface.");
Check(texts.Settings == "Настройки" && texts.SignIn == "Войти", "Russian UI strings are incomplete.");
Check(
    Enumerable.Range(0, 10).All(step =>
        !string.IsNullOrWhiteSpace(texts.OnboardingTitle(step)) &&
        !string.IsNullOrWhiteSpace(texts.OnboardingDescription(step)) &&
        !string.IsNullOrWhiteSpace(texts.OnboardingHint(step))),
    "Russian onboarding copy is incomplete.");
Check(
    TutorialCatalog.ContextGuides.All(guide =>
        Enumerable.Range(0, guide.Value).All(step =>
            !string.IsNullOrWhiteSpace(texts.ContextTutorialTitle(guide.Key, step)) &&
            !string.IsNullOrWhiteSpace(texts.ContextTutorialDescription(guide.Key, step)) &&
            !string.IsNullOrWhiteSpace(texts.ContextTutorialHint(guide.Key, step)))),
    "Russian contextual tutorial copy is incomplete.");
Check(texts.ViewerCount(1).EndsWith(" зритель", StringComparison.Ordinal) &&
      texts.ViewerCount(2).EndsWith(" зрителя", StringComparison.Ordinal) &&
      texts.ViewerCount(5).EndsWith(" зрителей", StringComparison.Ordinal) &&
      texts.ViewerCount(11).EndsWith(" зрителей", StringComparison.Ordinal) &&
      texts.ViewerCount(21).EndsWith(" зритель", StringComparison.Ordinal),
    "Russian viewer-count pluralization is incorrect.");
texts.SetLanguage("en");
Check(texts.Settings == "Settings" && texts.SignIn == "Sign in", "English UI strings are incomplete.");
Check(
    Enumerable.Range(0, 10).All(step =>
        !string.IsNullOrWhiteSpace(texts.OnboardingTitle(step)) &&
        !string.IsNullOrWhiteSpace(texts.OnboardingDescription(step)) &&
        !string.IsNullOrWhiteSpace(texts.OnboardingHint(step))),
    "English onboarding copy is incomplete.");
Check(
    TutorialCatalog.ContextGuides.All(guide =>
        Enumerable.Range(0, guide.Value).All(step =>
            !string.IsNullOrWhiteSpace(texts.ContextTutorialTitle(guide.Key, step)) &&
            !string.IsNullOrWhiteSpace(texts.ContextTutorialDescription(guide.Key, step)) &&
            !string.IsNullOrWhiteSpace(texts.ContextTutorialHint(guide.Key, step)))),
    "English contextual tutorial copy is incomplete.");
Check(texts.ViewerCount(1).EndsWith(" viewer", StringComparison.Ordinal) &&
      texts.ViewerCount(2).EndsWith(" viewers", StringComparison.Ordinal),
    "English viewer-count pluralization is incorrect.");

foreach (var property in typeof(UiText).GetProperties()
             .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0))
{
    Check(!string.IsNullOrWhiteSpace((string?)property.GetValue(texts)),
        "Localized UI text is empty: " + property.Name);
}

var option = new SelectionOptionViewModel("Dark", "Dark");
Check(option.ToString() == "Dark", "Selection options must render their localized label.");

var linkSegments = ChatLinkParser.Parse(
    "Telegram https://t.me/recrent?start=chat, VK vk.com/recrent, " +
    "news https://news.example.com/a/very/long/path, email user@example.com.");
Check(
    linkSegments.Count(segment => segment.IsLink) == 3 &&
    linkSegments.Any(segment => segment.Text == "Telegram · t.me" &&
                                segment.FullUrl == "https://t.me/recrent?start=chat") &&
    linkSegments.Any(segment => segment.Text == "VK · vk.com" &&
                                segment.FullUrl == "vk.com/recrent" &&
                                segment.Uri == new Uri("https://vk.com/recrent")) &&
    linkSegments.Any(segment => segment.Text == "news.example.com" &&
                                segment.FullUrl == "https://news.example.com/a/very/long/path"),
    "Chat links with and without a scheme must use short labels and retain their target URL.");

var bareDomainTargets = ChatLinkParser.Parse(
        "t.me/recrent youtube.com/c/RecrentChannel discord.gg/recrent recrentshop.ru/catalog.")
    .Where(segment => segment.IsLink)
    .Select(segment => segment.Uri!.AbsoluteUri)
    .ToArray();
Check(
    bareDomainTargets.SequenceEqual(
    [
        "https://t.me/recrent",
        "https://youtube.com/c/RecrentChannel",
        "https://discord.gg/recrent",
        "https://recrentshop.ru/catalog"
    ]),
    "Common bare Twitch-chat domains must open through HTTPS without swallowing punctuation.");

var idnLinks = ChatLinkParser.Parse("ссылка пример.рф/путь, но не user@пример.рф")
    .Where(segment => segment.IsLink)
    .ToArray();
Check(
    idnLinks.Length == 1 && idnLinks[0].Uri?.IdnHost == "xn--e1afmkfd.xn--p1ai",
    "Bare IDN links must open through HTTPS without matching email domains.");

using var imageCache = new ChatImageCache();
var message = new ChatMessage
{
    Id = "desktop-smoke",
    Channel = "witherchat",
    UserLogin = "tester",
    DisplayName = "Tester",
    Text = new string('x', 321),
    Timestamp = DateTimeOffset.UtcNow,
    ReplyParentDisplayName = "Parent",
    ReplyParentText = "Hello",
    Parts = [ChatMessagePart.PlainText(new string('x', 321))]
};
var item = new ChatMessageItemViewModel(message, imageCache, texts);
Check(item.IsLong && item.ReplyLabel == "Reply @Parent" && item.ExpandLabel == "Expand",
    "A localized long message row was initialized incorrectly.");
item.ToggleExpandedCommand.Execute(null);
Check(item.IsExpanded && item.ExpandLabel == "Collapse" && item.ShowRichContent,
    "Expanding a long message did not expose its rich content.");
item.MarkModerated(ChatMessageModerationState.Deleted);
Check(
    item.IsModerated &&
    item.ModerationStatus == "Message deleted" &&
    item.ContentOpacity == 0.48 &&
    item.ContentTextDecorations is not null,
    "Deleted messages must retain their content and use the 0.3.3 moderation presentation.");
texts.SetLanguage("ru");
item.RefreshLocalization();
Check(item.ModerationStatus == "Сообщение удалено",
    "Moderation status did not follow the selected UI language.");

var richMessage = new ChatMessage
{
    Id = "rich-emotes",
    Channel = "witherchat",
    UserLogin = "tester",
    DisplayName = "Tester",
    Text = "Kappa KEKW OMEGALUL",
    Timestamp = DateTimeOffset.UtcNow,
    Parts = TwitchEmoteParser.Parse("Kappa KEKW OMEGALUL", "25:0-4")
};
var richItem = new ChatMessageItemViewModel(
    richMessage,
    imageCache,
    texts,
    thirdPartyCatalog: new Dictionary<string, ThirdPartyEmote>
    {
        ["KEKW"] = new(
            "7tv-id",
            "KEKW",
            new Uri("https://cdn.7tv.app/emote/7tv-id/2x.png"),
            "7TV"),
        ["OMEGALUL"] = new(
            "bttv-id",
            "OMEGALUL",
            new Uri("https://cdn.betterttv.net/emote/bttv-id/2x"),
            ThirdPartyEmoteProviders.Bttv)
    });
Check(
    richItem.Parts.Count(part => part.Kind == ChatMessagePartKind.Emote) == 3 &&
    richItem.Parts.Any(part => part.Provider == "Twitch") &&
    richItem.Parts.Any(part => part.Provider == "7TV") &&
    richItem.Parts.Any(part => part.Provider == ThirdPartyEmoteProviders.Bttv),
    "Twitch, 7TV, and BetterTTV emotes must be presented together.");
richItem.ApplyPresentationSettings(
    enableTwitchEmotes: false,
    enableBttvEmotes: false,
    enableSevenTvEmotes: true,
    catalog: new Dictionary<string, ThirdPartyEmote>
    {
        ["KEKW"] = new(
            "7tv-id",
            "KEKW",
            new Uri("https://cdn.7tv.app/emote/7tv-id/2x.png"),
            "7TV"),
        ["OMEGALUL"] = new(
            "bttv-id",
            "OMEGALUL",
            new Uri("https://cdn.betterttv.net/emote/bttv-id/2x"),
            ThirdPartyEmoteProviders.Bttv)
    });
Check(
    richItem.Parts.Count(part => part.Kind == ChatMessagePartKind.Emote) == 1 &&
    richItem.Parts.Single(part => part.Kind == ChatMessagePartKind.Emote).Provider == "7TV",
    "Per-provider emote settings must disable Twitch and BetterTTV without hiding 7TV.");
Check(
    ThirdPartyEmoteProviders.IsBttv("BTTV") &&
    ThirdPartyEmoteProviders.IsBttv("BetterTTV") &&
    !ThirdPartyEmoteProviders.IsBttv("7TV"),
    "Current and legacy BetterTTV provider names must use the same settings switch.");

if (failures.Count == 0)
{
    Console.WriteLine("WitherChat desktop smoke tests passed.");
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
