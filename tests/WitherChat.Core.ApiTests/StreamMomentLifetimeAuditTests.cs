using System.Reflection;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class StreamMomentLifetimeAuditTests
{
    [Fact]
    public async Task DisposingDuringAcceptedSaveDoesNotBreakCompletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-lifetime-audit-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(directory);
        paths.EnsureCreated();
        var store = new StreamMomentStore(paths);
        var gate = (SemaphoreSlim)typeof(StreamMomentStore)
            .GetField("_saveGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var moment = new StreamMoment("first", ChatPlatforms.Twitch, "audit", "message",
            "Viewer", "message", "note", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var save = store.SaveAsync([moment], TestContext.Current.CancellationToken);
        try
        {
            store.Dispose();
            gate.Release();
            await save.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var reader = new StreamMomentStore(paths);
            Assert.Equal(moment, Assert.Single(reader.Load()));
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => store.SaveAsync([], TestContext.Current.CancellationToken));
        }
        finally
        {
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
