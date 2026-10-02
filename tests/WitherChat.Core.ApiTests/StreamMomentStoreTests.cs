using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class StreamMomentStoreTests
{
    [Fact]
    public async Task MomentsRoundTripAndRemainOrdered()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-moments-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new StreamMomentStore(new AppDataPaths(directory));
            var moments = new[]
            {
                new StreamMoment("1", ChatPlatforms.Twitch, "channel", "m1", "Viewer", "First", "note",
                    DateTimeOffset.Parse("2026-08-13T10:00:00Z"), DateTimeOffset.Parse("2026-08-13T10:01:00Z")),
                new StreamMoment("2", ChatPlatforms.YouTube, "youtube_channel", "m2", "Member", "Second", "",
                    DateTimeOffset.Parse("2026-08-13T10:02:00Z"), DateTimeOffset.Parse("2026-08-13T10:03:00Z"),
                    StreamEventKinds.SuperChat)
            };

            await store.SaveAsync(moments, TestContext.Current.CancellationToken);
            var loaded = store.Load();

            Assert.Equal(moments, loaded);
            Assert.Equal(StreamEventKinds.SuperChat, loaded[1].EventKind);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
