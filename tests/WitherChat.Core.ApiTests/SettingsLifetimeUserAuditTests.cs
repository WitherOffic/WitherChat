using System.Reflection;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class SettingsLifetimeUserAuditTests
{
    [Fact]
    public async Task AcceptedQueuedSaveSurvivesSynchronousDisposal()
    {
        using var owned = new OwnedDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new SettingsStore(owned.Paths);
        var gate = (SemaphoreSlim)typeof(SettingsStore).GetField("_saveLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync(cancellation.Token);
        var save = store.SaveAsync(new WitherChatSettings { Theme = "Light", MessageLimit = 666 },
            cancellation.Token);
        try
        {
            Assert.False(save.IsCompleted);
            store.Dispose();
            Assert.Null(Record.Exception(() => gate.Release()));
            await save.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
            using var reader = new SettingsStore(owned.Paths);
            Assert.Equal("Light", reader.Load().Theme);
            Assert.Equal(666, reader.Load().MessageLimit);
            Assert.False(File.Exists(owned.Paths.SettingsFile + ".tmp"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SaveAsync(
                new WitherChatSettings(), TestContext.Current.CancellationToken));
        }
        finally
        {
            await cancellation.CancelAsync();
            await Record.ExceptionAsync(() => save.WaitAsync(TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            store.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncDisposalWaitsForAllAcceptedOrCancelledWrites(bool cancel)
    {
        using var owned = new OwnedDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new SettingsStore(owned.Paths);
        var gate = (SemaphoreSlim)typeof(SettingsStore).GetField("_saveLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync(cancellation.Token);
        var writes = Enumerable.Range(0, 4).Select(index => store.SaveAsync(
            new WitherChatSettings { MessageLimit = 500 + index }, cancellation.Token)).ToArray();
        try
        {
            var first = store.DisposeAsync().AsTask();
            var second = store.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            if (cancel)
                await cancellation.CancelAsync();
            else
                gate.Release();
            await Task.WhenAll(first, second).WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(writes));
            else
            {
                await Task.WhenAll(writes);
                using var reader = new SettingsStore(owned.Paths);
                Assert.Equal(503, reader.Load().MessageLimit);
            }
            Assert.Throws<ObjectDisposedException>(() => store.Load());
            Assert.False(File.Exists(owned.Paths.SettingsFile + ".tmp"));
        }
        finally
        {
            await cancellation.CancelAsync();
            await Record.ExceptionAsync(() => Task.WhenAll(writes).WaitAsync(
                TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            store.Dispose();
        }
    }

    private sealed class OwnedDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "WitherChat-settings-lifetime-audit-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths { get; }
        public OwnedDirectory()
        {
            Paths = new AppDataPaths(_root);
            Paths.EnsureCreated();
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(_root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary path escaped its root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
