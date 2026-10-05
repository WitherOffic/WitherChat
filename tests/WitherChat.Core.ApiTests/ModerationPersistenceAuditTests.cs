using System.Reflection;
using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class ModerationPersistenceAuditTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullRowsAreSkippedWithoutLosingValidModerationRows(bool bannedRows)
    {
        using var owned = new OwnedDirectory();
        var channel = ValidChannel();
        if (bannedRows) channel.BannedUsers.Insert(0, null!);
        else channel.UnbanRequests.Insert(0, null!);
        await SeedAsync(owned.Paths, channel);
        await using var cache = new ModerationCacheStore(owned.Paths);
        var snapshot = cache.Restore("100");
        Assert.Equal("viewer", Assert.Single(snapshot.BannedUsers).UserLogin);
        Assert.Equal("request", Assert.Single(snapshot.UnbanRequests).RequestId);
    }

    [Fact]
    public async Task OptionalNullStringsAreNormalizedBeforeReturningModels()
    {
        using var owned = new OwnedDirectory();
        var channel = ValidChannel();
        var ban = channel.BannedUsers[0];
        ban.UserLogin = null!;
        ban.DisplayName = null!;
        ban.Reason = null!;
        var request = channel.UnbanRequests[0];
        request.BroadcasterId = null!;
        request.UserId = null!;
        request.UserLogin = null!;
        request.DisplayName = null!;
        request.RequestText = null!;
        request.ResolutionText = null!;
        await SeedAsync(owned.Paths, channel);
        await using var cache = new ModerationCacheStore(owned.Paths);
        var snapshot = cache.Restore("100");
        var restoredBan = Assert.Single(snapshot.BannedUsers);
        var restoredRequest = Assert.Single(snapshot.UnbanRequests);
        Assert.Equal(string.Empty, restoredBan.UserLogin);
        Assert.Equal(string.Empty, restoredBan.DisplayName);
        Assert.Equal(string.Empty, restoredBan.Reason);
        Assert.Equal(string.Empty, restoredRequest.BroadcasterId);
        Assert.Equal(string.Empty, restoredRequest.UserId);
        Assert.Equal(string.Empty, restoredRequest.UserLogin);
        Assert.Equal(string.Empty, restoredRequest.DisplayName);
        Assert.Equal(string.Empty, restoredRequest.RequestText);
        Assert.Equal(string.Empty, restoredRequest.ResolutionText);
    }

    [Fact]
    public async Task EveryConcurrentDisposeWaitsForTheFinalSave()
    {
        using var owned = new OwnedDirectory();
        var cache = new ModerationCacheStore(owned.Paths);
        var gate = Private<SemaphoreSlim>(cache, "_writeGate");
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Task? first = null;
        try
        {
            cache.ScheduleSave("100", [Ban()], [Request()]);
            first = cache.DisposeAsync().AsTask();
            var second = cache.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            gate.Release();
            gate = null!;
            await Task.WhenAll(first, second).WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Throws<ObjectDisposedException>(() => cache.ScheduleSave("100", [], []));
            Assert.Throws<ObjectDisposedException>(() => cache.Restore("100"));
            await using var restored = new ModerationCacheStore(owned.Paths);
            Assert.Single(restored.Restore("100").BannedUsers);
        }
        finally
        {
            gate?.Release();
            if (first is not null)
                await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await cache.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARecoveredWriteFailureDoesNotPreventTheFinalSave()
    {
        using var owned = new OwnedDirectory();
        await File.WriteAllTextAsync(owned.Paths.ModerationCacheFile, "{}",
            TestContext.Current.CancellationToken);
        var cache = new ModerationCacheStore(owned.Paths);
        try
        {
            using (var fileLock = new FileStream(owned.Paths.ModerationCacheFile,
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                cache.ScheduleSave("100", [Ban()], [Request()]);
                var pending = Private<Task>(cache, "_pendingSave");
                var failure = await Record.ExceptionAsync(() => pending.WaitAsync(
                    TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.True(failure is IOException or UnauthorizedAccessException);
            }
            await cache.DisposeAsync();
            await using var restored = new ModerationCacheStore(owned.Paths);
            Assert.Single(restored.Restore("100").BannedUsers);
            Assert.Single(restored.Restore("100").UnbanRequests);
        }
        finally
        {
            await cache.DisposeAsync();
        }
    }

    [Fact]
    public async Task RestartPreservesValidRowsAndDiscardsExpiredOrResolvedRows()
    {
        using var owned = new OwnedDirectory();
        var channel = ValidChannel();
        channel.BannedUsers.Add(ModerationCacheStore.CachedBannedUser.FromModel(
            Ban() with { UserId = "expired", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        channel.BannedUsers.Add(ModerationCacheStore.CachedBannedUser.FromModel(
            Ban() with { UserId = string.Empty }));
        channel.UnbanRequests.Add(ModerationCacheStore.CachedUnbanRequest.FromModel(
            Request() with { RequestId = "resolved", Status = UnbanRequestStatus.Approved }));
        channel.UnbanRequests.Add(ModerationCacheStore.CachedUnbanRequest.FromModel(
            Request() with { RequestId = string.Empty }));
        await SeedAsync(owned.Paths, channel);
        await using (var cache = new ModerationCacheStore(owned.Paths))
        {
            Assert.Single(cache.Restore("100").BannedUsers);
            Assert.Single(cache.Restore("100").UnbanRequests);
        }
        await using var restored = new ModerationCacheStore(owned.Paths);
        Assert.Single(restored.Restore("100").BannedUsers);
        Assert.Single(restored.Restore("100").UnbanRequests);
        if (OperatingSystem.IsWindows())
        {
            var bytes = await File.ReadAllBytesAsync(owned.Paths.ModerationCacheFile,
                TestContext.Current.CancellationToken);
            Assert.NotEqual((byte)'{', bytes[0]);
        }
    }

    private static async Task SeedAsync(AppDataPaths paths, ModerationCacheStore.CacheChannel channel)
    {
        var document = new ModerationCacheStore.CacheDocument();
        document.Channels["100"] = channel;
        await File.WriteAllTextAsync(paths.ModerationCacheFile,
            JsonSerializer.Serialize(document, JsonOptions), TestContext.Current.CancellationToken);
    }

    private static ModerationCacheStore.CacheChannel ValidChannel() => new()
    {
        LastUpdatedAt = DateTimeOffset.UtcNow,
        BannedUsers = [ModerationCacheStore.CachedBannedUser.FromModel(Ban())],
        UnbanRequests = [ModerationCacheStore.CachedUnbanRequest.FromModel(Request())]
    };

    private static BannedUser Ban() => new("42", "viewer", "Viewer",
        DateTimeOffset.UtcNow, null, "reason");

    private static UnbanRequest Request() => new("request", "100", "42", "viewer", "Viewer",
        "please", DateTimeOffset.UtcNow, UnbanRequestStatus.Pending, null, string.Empty);

    private static T Private<T>(object instance, string name) =>
        (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance) ?? throw new InvalidOperationException("Missing field: " + name));

    private sealed class OwnedDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "WitherChat-moderation-audit-" + Guid.NewGuid().ToString("N"));
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
