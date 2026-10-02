using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class EventSubResilienceTests
{
    [Fact]
    public async Task ChatClearSubscriptionAndNotificationPreserveTargetChannel()
    {
        Assert.Contains("channel.chat.clear", TwitchEventSubClient.UserChatSubscriptionTypes);
        using var document = JsonDocument.Parse("""
            {
              "metadata": {
                "subscription_type": "channel.chat.clear",
                "message_timestamp": "2026-08-13T10:11:12Z"
              },
              "payload": {
                "event": {
                  "broadcaster_user_id": "100",
                  "broadcaster_user_login": "Target_Channel",
                  "broadcaster_user_name": "Target Channel"
                }
              }
            }
            """);
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId);
        await using var eventSub = new TwitchEventSubClient(api);
        EventSubChatCleared? received = null;
        eventSub.ChatCleared += (_, eventArgs) => received = eventArgs.Value;

        eventSub.HandleNotification(document.RootElement);

        Assert.NotNull(received);
        Assert.Equal("100", received.BroadcasterId);
        Assert.Equal("Target_Channel", received.BroadcasterLogin);
        Assert.Equal(DateTimeOffset.Parse("2026-08-13T10:11:12Z"), received.ClearedAt);
    }

    [Fact]
    public void ChatClearWithoutBroadcasterLoginIsRejected()
    {
        using var document = JsonDocument.Parse("""{"broadcaster_user_id":"100"}""");

        Assert.Null(TwitchEventSubClient.ParseChatClear(document.RootElement));
    }

    [Fact]
    public void AnimatedTwitchFragmentUsesAnimatedCdnVariant()
    {
        using var document = JsonDocument.Parse("""
            {"message_id":"message-1","broadcaster_user_login":"channel",
             "broadcaster_user_id":"1","chatter_user_id":"2",
             "chatter_user_login":"viewer","chatter_user_name":"Viewer",
             "message":{"text":"Kappa","fragments":[{"type":"emote","text":"Kappa",
               "emote":{"id":"25","format":["static","animated"]}}]}}
            """);

        var message = TwitchEventSubClient.ParseChatMessage(document.RootElement);

        var emote = Assert.Single(message!.Parts);
        Assert.Contains("/animated/dark/2.0", emote.ImageUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventSubRejectsDuplicateDeliveryIdsAndBoundsItsCache()
    {
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId);
        await using var eventSub = new TwitchEventSubClient(api);

        Assert.True(eventSub.RememberMessageId("message-1"));
        Assert.False(eventSub.RememberMessageId("message-1"));
        Assert.True(eventSub.RememberMessageId(string.Empty));

        for (var index = 2; index <= 2_050; index++)
        {
            Assert.True(eventSub.RememberMessageId($"message-{index}"));
        }

        Assert.True(eventSub.RememberMessageId("message-1"));
    }

    [Fact]
    public async Task ChatNotificationProducesSubscriptionEventAndSystemMessage()
    {
        Assert.Contains("channel.chat.notification", TwitchEventSubClient.UserChatSubscriptionTypes);
        using var document = JsonDocument.Parse("""
            {"metadata":{"subscription_type":"channel.chat.notification","message_timestamp":"2026-08-13T12:00:00Z"},
             "payload":{"event":{"broadcaster_user_id":"100","broadcaster_user_login":"channel",
             "chatter_user_id":"200","chatter_user_login":"viewer","chatter_user_name":"Viewer",
             "message_id":"notice-1","notice_type":"sub","message":{"text":"Glad to be here","fragments":[]},
             "sub":{"sub_tier":"1000","is_prime":true,"duration_months":1}}}}
            """);
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId);
        await using var eventSub = new TwitchEventSubClient(api);
        StreamEvent? streamEvent = null;
        ChatMessage? systemMessage = null;
        eventSub.StreamEventReceived += (_, eventArgs) => streamEvent = eventArgs.Value;
        eventSub.MessageReceived += (_, eventArgs) => systemMessage = eventArgs.Message;

        eventSub.HandleNotification(document.RootElement);

        Assert.NotNull(streamEvent);
        Assert.Equal(StreamEventKinds.Subscription, streamEvent.Kind);
        Assert.Equal("Viewer", streamEvent.DisplayName);
        Assert.NotNull(systemMessage);
        Assert.Same(streamEvent, systemMessage.StreamEvent);
        Assert.True(systemMessage.IsSystemEvent);
    }

    [Fact]
    public async Task EventSubRejectsNullChannelCollections()
    {
        using var api = new TwitchChatApiClient(TwitchApplication.ClientId);
        await using var eventSub = new TwitchEventSubClient(api);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            eventSub.ConfigureAsync(null, null!, TestContext.Current.CancellationToken));
    }
}
