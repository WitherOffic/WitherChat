using System.Reflection;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class StreamMomentDrainAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncDisposalDrainsAcceptedSavesAndRejectsNewOnes(bool cancelFirst)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-drain-audit-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(directory);
        paths.EnsureCreated();
        var store = new StreamMomentStore(paths);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var gate = (SemaphoreSlim)typeof(StreamMomentStore)
            .GetField("_saveGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var moment = new StreamMoment("first", ChatPlatforms.Twitch, "audit", "message",
            "Viewer", "message", "note", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var first = store.SaveAsync([moment], cancellation.Token);
        var second = store.SaveAsync([moment with { Id = "second" }], TestContext.Current.CancellationToken);
        try
        {
            if (cancelFirst) await cancellation.CancelAsync();
            var disposing = store.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => store.SaveAsync([], TestContext.Current.CancellationToken));
            gate.Release();
            if (cancelFirst) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            else await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await disposing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await store.DisposeAsync();
            using var reader = new StreamMomentStore(paths);
            Assert.Equal("second", Assert.Single(reader.Load()).Id);
            Assert.False(File.Exists(paths.MomentsFile + ".tmp"));
            Assert.Throws<ObjectDisposedException>(() => store.Load());
        }
        finally
        {
            store.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
