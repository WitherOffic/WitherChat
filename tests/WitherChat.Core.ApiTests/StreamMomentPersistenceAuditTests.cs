using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class StreamMomentPersistenceAuditTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task CorruptFileIsPreservedBeforeNewSave(string json)
    {
        using var files = new MomentFiles();
        File.WriteAllText(files.Paths.MomentsFile, json);
        using var store = new StreamMomentStore(files.Paths);
        Assert.Empty(store.Load());
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        var preserved = Assert.Single(Directory.GetFiles(files.DirectoryPath, "stream-moments.json.corrupt-*"));
        Assert.Equal(json, File.ReadAllText(preserved));
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
    }

    [Fact]
    public async Task NullEntriesAreIgnoredWithoutDiscardingValidMoments()
    {
        using var files = new MomentFiles();
        File.WriteAllText(files.Paths.MomentsFile,
            JsonSerializer.Serialize(new StreamMoment?[] { null, files.Moment }));
        using var store = new StreamMomentStore(files.Paths);
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
        await store.SaveAsync(new StreamMoment[] { null!, files.Moment }, TestContext.Current.CancellationToken);
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
    }

    [Fact]
    public async Task CorruptPrimaryRecoversLastGoodBackup()
    {
        using var files = new MomentFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        await store.SaveAsync([files.Moment with { Id = "second" }], TestContext.Current.CancellationToken);
        File.WriteAllText(files.Paths.MomentsFile, "{");
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
        Assert.Equal("{", File.ReadAllText(Assert.Single(
            Directory.GetFiles(files.DirectoryPath, "stream-moments.json.corrupt-*"))));
        using var restarted = new StreamMomentStore(files.Paths);
        Assert.Equal(files.Moment, Assert.Single(restarted.Load()));
    }

    [Fact]
    public async Task FailedReadCannotBeFollowedByDestructiveEmptySave()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows file-sharing semantics.
        using var files = new MomentFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        using (var locked = new FileStream(files.Paths.MomentsFile, FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
            Assert.Empty(store.Load());
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
        await store.SaveAsync([], TestContext.Current.CancellationToken);
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task FailedAtomicReplaceRetainsPreviousDataAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows atomic-replace sharing semantics.
        using var files = new MomentFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        using (var locked = new FileStream(files.Paths.MomentsFile, FileMode.Open,
                   FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => store.SaveAsync([], TestContext.Current.CancellationToken));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.False(File.Exists(files.Paths.MomentsFile + ".tmp"));
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
    }

    [Fact]
    public async Task CancellationDoesNotChangePreviousData()
    {
        using var files = new MomentFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync([], cancellation.Token));
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
        Assert.False(File.Exists(files.Paths.MomentsFile + ".tmp"));
    }

    [Fact]
    public async Task StoreLimitRemainsFiveThousandValidEntries()
    {
        using var files = new MomentFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync(Enumerable.Range(0, 5005)
            .Select(i => files.Moment with { Id = i.ToString() }).ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(5000, store.Load().Count);
        Assert.Equal("5", store.Load()[0].Id);
    }

    [Fact]
    public async Task LockedBackupCannotBeDiscardedDuringCorruptPrimaryRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new MomentFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        await store.SaveAsync([files.Moment with { Id = "second" }], TestContext.Current.CancellationToken);
        File.WriteAllText(files.Paths.MomentsFile, "{");
        using (var locked = new FileStream(files.Paths.MomentsFile + ".bak", FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
            Assert.Empty(store.Load());
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([], TestContext.Current.CancellationToken));
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
        await store.SaveAsync([files.Moment], TestContext.Current.CancellationToken);
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
    }
    private sealed class MomentFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "WitherChat-moment-audit-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths { get; }
        public StreamMoment Moment { get; } = new("first", ChatPlatforms.Twitch,
            "audit", "message", "Viewer", "Привет 👋", "note",
            DateTimeOffset.Parse("2026-10-03T01:00:00Z"),
            DateTimeOffset.Parse("2026-10-03T01:01:00Z"));

        public MomentFiles()
        {
            Paths = new AppDataPaths(DirectoryPath);
            Paths.EnsureCreated();
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
