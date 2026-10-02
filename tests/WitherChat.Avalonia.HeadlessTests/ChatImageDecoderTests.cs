using System.Collections.Concurrent;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using WitherChat.Core.Models;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ChatImageDecoderTests
{
    private const string TwoFrameGif =
        "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
        "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7";

    [AvaloniaFact]
    public void AnimatedGifProducesAllFrames()
    {
        using var media = ChatImageDecoder.Decode(Convert.FromBase64String(TwoFrameGif));

        Assert.NotNull(media);
        Assert.True(media!.IsAnimated);
        Assert.Equal(2, media.Frames.Count);
        Assert.Equal(media.Frames.Count, media.FrameDelays.Count);
        Assert.All(media.FrameDelays, delay => Assert.True(delay >= TimeSpan.FromMilliseconds(20)));
    }

    [AvaloniaFact]
    public async Task StartupPreloadFinishesAndMissingImagesAreNegativeCached()
    {
        var handler = new ImageHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var valid = new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/2.0");
        var missing = new Uri("https://static-cdn.jtvnw.net/missing/image.gif");

        await cache.PreloadAsync([valid, missing]);

        Assert.False(cache.IsLoading);
        Assert.True(cache.GetResource(valid)!.HasImage);
        Assert.False(cache.GetResource(missing)!.HasImage);
        _ = cache.GetResource(missing);
        await Task.Delay(30);
        Assert.Equal(1, handler.Requests[missing.AbsoluteUri]);
    }

    [AvaloniaFact]
    public async Task YouTubeAvatarCdnIsAllowedButUnrelatedHostsRemainBlocked()
    {
        var handler = new ImageHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var youtubeAvatar = new Uri("https://yt3.ggpht.com/channel-avatar=s88-c-k-c0x00ffffff-no-rj");

        await cache.PreloadAsync([youtubeAvatar]);

        Assert.True(cache.GetResource(youtubeAvatar)!.HasImage);
        Assert.Null(cache.GetResource(new Uri("https://example.test/untrusted.png")));
        Assert.Equal(1, handler.Requests[youtubeAvatar.AbsoluteUri]);
    }

    [AvaloniaFact]
    public async Task TransientImageFailureIsRetriedButPermanentFailureRemainsCached()
    {
        var imageBytes = Convert.FromBase64String(TwoFrameGif);
        var handler = new TransientImageHandler(imageBytes);
        using var cache = new ChatImageCache(handler);
        var transient = new Uri("https://static-cdn.jtvnw.net/transient/image.gif");
        var missing = new Uri("https://static-cdn.jtvnw.net/missing/image.gif");

        await cache.PreloadAsync([transient, missing]);
        var originalTransientResource = cache.GetResource(transient)!;
        await cache.PreloadAsync([transient, missing]);

        Assert.True(originalTransientResource.HasImage);
        Assert.Same(originalTransientResource, cache.GetResource(transient));
        Assert.False(cache.GetResource(missing)!.HasImage);
        Assert.Equal(2, handler.Requests[transient.AbsoluteUri]);
        Assert.Equal(1, handler.Requests[missing.AbsoluteUri]);
    }

    [AvaloniaFact]
    public async Task TimedOutImageRequestIsRetriedOnTheSameResource()
    {
        var handler = new TimeoutThenImageHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var uri = new Uri("https://static-cdn.jtvnw.net/timeout/image.gif");
        var resource = cache.GetResource(uri)!;

        var preload = cache.PreloadAsync([uri]);
        handler.CompleteTimeout.TrySetResult();
        await preload;

        Assert.False(cache.IsLoading);
        Assert.False(resource.HasImage);
        await cache.PreloadAsync([uri]);
        Assert.Same(resource, cache.GetResource(uri));
        Assert.True(resource.HasImage);
        Assert.Equal(2, handler.Requests);
    }

    [AvaloniaFact]
    public async Task StalledImageBodiesTimeOutAndReleaseAllDownloadSlots()
    {
        var handler = new StalledBodyHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler, TimeSpan.FromMilliseconds(200));
        var stalled = Enumerable.Range(0, 8)
            .Select(index => new Uri($"https://static-cdn.jtvnw.net/stalled/{index}.gif"))
            .ToArray();

        await cache.PreloadAsync(stalled).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(cache.IsLoading);
        Assert.Equal(8, handler.StalledRequests);
        var healthy = new Uri("https://static-cdn.jtvnw.net/healthy/image.gif");
        await cache.PreloadAsync([healthy]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cache.GetResource(healthy)!.HasImage);
        Assert.False(cache.IsLoading);
    }

    [AvaloniaFact]
    public async Task DisposingImageCacheCancelsStalledBodyWithoutFaultingPreload()
    {
        var handler = new StalledBodyHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var uri = new Uri("https://static-cdn.jtvnw.net/stalled/dispose.gif");
        var preload = cache.PreloadAsync([uri]);
        await handler.BodyReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cache.Dispose();
        await preload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(cache.IsLoading);
        Assert.Null(cache.GetResource(uri));
        Assert.Equal(1, handler.StalledRequests);
    }

    [AvaloniaFact]
    public async Task ImageCacheEvictsOldEntriesInsteadOfRejectingNewImagesForever()
    {
        var handler = new ImageHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var initial = Enumerable.Range(0, 1_024)
            .Select(index => new Uri($"https://static-cdn.jtvnw.net/missing/{index}.gif"))
            .ToArray();
        await cache.PreloadAsync(initial);
        var newest = new Uri("https://static-cdn.jtvnw.net/emoticons/v2/new/default/dark/2.0");

        await cache.PreloadAsync([newest]);

        Assert.True(cache.GetResource(newest)!.HasImage);
        Assert.Equal(1, handler.Requests[newest.AbsoluteUri]);
    }

    [AvaloniaFact]
    public void ImageCacheAtCapacityWithEveryEntryLoadingRejectsTheNextImageWithoutThrowing()
    {
        using var cache = new ChatImageCache(new BlockingImageHandler());
        for (var index = 0; index < 1_024; index++)
        {
            Assert.NotNull(cache.GetResource(new Uri(
                $"https://static-cdn.jtvnw.net/loading/{index}.gif")));
        }

        var overflow = Record.Exception(() => cache.GetResource(
            new Uri("https://static-cdn.jtvnw.net/loading/overflow.gif")));

        Assert.Null(overflow);
        Assert.Null(cache.GetResource(
            new Uri("https://static-cdn.jtvnw.net/loading/overflow.gif")));
    }

    [AvaloniaFact]
    public async Task ChatLogEntryRestoresBadgesUserColorAndRichEmotes()
    {
        var handler = new ImageHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var badgeUri = new Uri("https://static-cdn.jtvnw.net/badges/v1/mod/2");
        var twitchUri = new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0");
        var sevenTvUri = new Uri("https://cdn.7tv.app/emote/seven-1/2x.webp");
        var bttvUri = new Uri("https://cdn.betterttv.net/emote/bttv-1/2x");
        await cache.PreloadAsync([badgeUri, twitchUri, sevenTvUri, bttvUri]);
        var entry = ChatLogEntryViewModel.Parse(
            """
            {"timestamp":"2026-07-29T00:00:01Z","displayName":"Tester","userLogin":"tester",
             "text":"Kappa hello KEKW OMEGALUL","color":"#00FF7F","broadcasterId":"100",
             "badges":[{"SetId":"moderator","VersionId":"1","Info":"",
                        "ImageUri":"https://static-cdn.jtvnw.net/badges/v1/mod/2","Title":"Moderator"}],
             "parts":[
               {"Kind":1,"Text":"Kappa","ImageUri":"https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0",
                "Provider":"Twitch","EmoteId":"25","SourceWidth":28,"SourceHeight":28,"IsZeroWidth":false},
               {"Kind":0,"Text":" hello KEKW OMEGALUL","ImageUri":null,
                "Provider":"","EmoteId":"","SourceWidth":0,"SourceHeight":0,"IsZeroWidth":false}
             ]}
            """);
        var thirdParty = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal)
        {
            ["KEKW"] = new ThirdPartyEmote(
                "seven-1", "KEKW", sevenTvUri, ThirdPartyEmoteProviders.SevenTv),
            ["OMEGALUL"] = new ThirdPartyEmote(
                "bttv-1", "OMEGALUL", bttvUri, ThirdPartyEmoteProviders.Bttv)
        };

        entry.ApplyPresentation(cache, null, thirdParty, true, true, true);

        Assert.Equal("moderator", entry.Role);
        Assert.Equal("100", entry.BroadcasterId);
        Assert.Equal("moderator/1", entry.BadgeData.Single().Key);
        Assert.Single(entry.Badges);
        Assert.True(entry.Badges[0].HasImageResource);
        Assert.Equal(3, entry.Parts.Count(part => !part.IsText));
        Assert.All(entry.Parts.Where(part => !part.IsText), part =>
            Assert.True(part.ImageResource?.HasImage));
        Assert.Equal(
            Color.Parse("#00FF7F"),
            Assert.IsType<SolidColorBrush>(entry.UserBrush).Color);

        entry.ApplyPresentation(cache, null, thirdParty, true, false, true);
        Assert.Equal(2, entry.Parts.Count(part => !part.IsText));
        Assert.DoesNotContain(entry.Parts, part => ThirdPartyEmoteProviders.IsBttv(part.Provider));
    }

    [AvaloniaFact]
    public async Task LargeChatLogParsedOnWorkerThreadRendersWithoutCrossThreadBrushes()
    {
        var entries = await Task.Run(() => Enumerable.Range(0, 3_000)
            .Select(index => ChatLogEntryViewModel.Parse(
                $$"""
                {"timestamp":"2026-07-29T00:00:01Z","displayName":"Viewer{{index}}",
                 "userLogin":"viewer{{index}}","text":"Message {{index}}","color":"",
                 "badges":[],"parts":[]}
                """))
            .ToArray());
        Assert.All(entries, entry => Assert.False(entry.HasPresentation));
        using var cache = new ChatImageCache(new ImageHandler(Convert.FromBase64String(TwoFrameGif)));
        foreach (var entry in entries)
        {
            entry.ApplyPresentation(cache, null, null, true, true, true);
        }

        Assert.All(entries, entry => Assert.True(entry.HasPresentation));
        var window = new Window
        {
            Width = 320,
            Height = 120,
            Content = new TextBlock
            {
                Text = entries[0].Text,
                Foreground = entries[0].UserBrush
            }
        };
        window.Show();
        window.Measure(new Size(320, 120));
        window.Arrange(new Rect(0, 0, 320, 120));
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LargeLiveChatHydratesOnlyRealizedRichContent()
    {
        var handler = new ImageHandler(Convert.FromBase64String(TwoFrameGif));
        using var cache = new ChatImageCache(handler);
        var texts = new UiText();
        var emoteUri = new Uri("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/2.0");
        var badgeUri = new Uri("https://static-cdn.jtvnw.net/badges/v1/mod/2");
        var items = Enumerable.Range(0, 10_000)
            .Select(index => new ChatMessageItemViewModel(
                new ChatMessage
                {
                    Id = "live-" + index,
                    Channel = "witherchat",
                    UserLogin = "viewer" + index,
                    DisplayName = "Viewer " + index,
                    Text = "Kappa message " + index,
                    Timestamp = DateTimeOffset.UtcNow,
                    Badges = [new ChatBadge("moderator", "1", ImageUri: badgeUri)],
                    Parts =
                    [
                        new ChatMessagePart
                        {
                            Kind = ChatMessagePartKind.Emote,
                            Text = "Kappa",
                            ImageUri = emoteUri,
                            Provider = "Twitch",
                            EmoteId = "25",
                            SourceWidth = 28,
                            SourceHeight = 28
                        },
                        ChatMessagePart.PlainText(" message " + index)
                    ]
                },
                cache,
                texts))
            .ToArray();

        Assert.All(items, item =>
        {
            Assert.False(item.HasHydratedBadges);
            Assert.False(item.HasHydratedParts);
        });
        Assert.Empty(handler.Requests);

        _ = items[0].Badges;
        _ = items[0].Parts;
        await cache.PreloadAsync([badgeUri, emoteUri]);

        Assert.True(items[0].HasHydratedBadges);
        Assert.True(items[0].HasHydratedParts);
        Assert.All(items.Skip(1), item =>
        {
            Assert.False(item.HasHydratedBadges);
            Assert.False(item.HasHydratedParts);
        });
        Assert.Equal(1, handler.Requests[badgeUri.AbsoluteUri]);
        Assert.Equal(1, handler.Requests[emoteUri.AbsoluteUri]);
    }

    private sealed class TimeoutThenImageHandler(byte[] gifBytes) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public TaskCompletionSource CompleteTimeout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                await CompleteTimeout.Task.WaitAsync(cancellationToken);
                throw new TaskCanceledException("The CDN request timed out.", new TimeoutException());
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(gifBytes)
            };
        }
    }

    private sealed class StalledBodyHandler(byte[] gifBytes) : HttpMessageHandler
    {
        private int _stalledRequests;
        public int StalledRequests => Volatile.Read(ref _stalledRequests);
        public TaskCompletionSource BodyReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.Contains("/stalled/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _stalledRequests);
                content = new StreamContent(new StalledReadStream(() => BodyReadStarted.TrySetResult()));
            }
            else
            {
                content = new ByteArrayContent(gifBytes);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StalledReadStream(Action readStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ImageHandler(byte[] gifBytes) : HttpMessageHandler
    {
        public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.AddOrUpdate(url, 1, (_, count) => count + 1);
            var response = url.Contains("/missing/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(gifBytes)
                };
            return Task.FromResult(response);
        }
    }

    private sealed class TransientImageHandler(byte[] gifBytes) : HttpMessageHandler
    {
        public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            var count = Requests.AddOrUpdate(url, 1, (_, current) => current + 1);
            if (url.Contains("/missing/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(count == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(gifBytes)
                });
        }
    }

    private sealed class BlockingImageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The image request should only end through cancellation.");
        }
    }
}
