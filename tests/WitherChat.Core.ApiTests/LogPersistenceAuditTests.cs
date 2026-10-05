using System.Globalization;
using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class LogPersistenceAuditTests
{
    [Theory]
    [InlineData("null", false)]
    [InlineData("null", true)]
    [InlineData("\"\"", false)]
    [InlineData("\"   \"", true)]
    [InlineData("\"broadcaster\"", false)]
    public async Task InvalidMetadataBroadcasterDoesNotStopTheWriter(string broadcasterJson, bool pascalCase)
    {
        using var owned = new OwnedDirectory();
        var message = Message("one");
        var session = SessionDirectory(owned.Path, message);
        Directory.CreateDirectory(session);
        await File.WriteAllTextAsync(System.IO.Path.Combine(session, "metadata.json"),
            "{\"" + (pascalCase ? "BroadcasterId" : "broadcasterId") + "\":" + broadcasterJson +
            ",\"" + (pascalCase ? "MessageCount" : "messageCount") + "\":7}",
            TestContext.Current.CancellationToken);
        var writer = new ChatLogWriter(owned.Path);
        Assert.True(writer.Enqueue(message));
        await writer.DisposeAsync();
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Combine(session, "metadata.json"), TestContext.Current.CancellationToken));
        Assert.Equal("broadcaster", metadata.RootElement.GetProperty("broadcasterId").GetString());
        Assert.Equal(8, metadata.RootElement.GetProperty("messageCount").GetInt64());
        Assert.Single(await File.ReadAllLinesAsync(
            System.IO.Path.Combine(session, "chat.jsonl"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-10L, 1L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public async Task InvalidMetadataCountDoesNotWrapOrRemainNegative(long oldCount, long expected)
    {
        using var owned = new OwnedDirectory();
        var message = Message("one");
        var session = SessionDirectory(owned.Path, message);
        Directory.CreateDirectory(session);
        await File.WriteAllTextAsync(System.IO.Path.Combine(session, "metadata.json"),
            "{\"BroadcasterId\":\"broadcaster\",\"MessageCount\":" +
            oldCount.ToString(CultureInfo.InvariantCulture) + "}", TestContext.Current.CancellationToken);
        var writer = new ChatLogWriter(owned.Path);
        Assert.True(writer.Enqueue(message));
        await writer.DisposeAsync();
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Combine(session, "metadata.json"), TestContext.Current.CancellationToken));
        Assert.Equal(expected, metadata.RootElement.GetProperty("messageCount").GetInt64());
    }

    [Fact]
    public async Task WriterRestartsAccumulateCountsAndPreserveTheOriginalMetadata()
    {
        using var owned = new OwnedDirectory();
        var first = Message("first");
        for (var index = 0; index < 3; index++)
        {
            await using var writer = new ChatLogWriter(owned.Path);
            Assert.True(writer.Enqueue(first with { Id = index.ToString(CultureInfo.InvariantCulture) }));
        }
        var session = SessionDirectory(owned.Path, first);
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(
            System.IO.Path.Combine(session, "metadata.json"), TestContext.Current.CancellationToken));
        var root = metadata.RootElement;
        Assert.Equal(3, root.GetProperty("messageCount").GetInt64());
        Assert.Equal("daily", root.GetProperty("logMode").GetString());
        Assert.Equal("audit", root.GetProperty("channelLogin").GetString());
        Assert.Equal("audit", root.GetProperty("channelDisplayName").GetString());
        Assert.Equal(first.Timestamp.ToUniversalTime(),
            root.GetProperty("logStartedAtUtc").GetDateTimeOffset());
        Assert.Equal(first.Timestamp.ToLocalTime(),
            root.GetProperty("logStartedAtLocal").GetDateTimeOffset());
        Assert.Equal(AppVersion.Current, root.GetProperty("appVersion").GetString());
        Assert.Equal(3, (await File.ReadAllLinesAsync(
            System.IO.Path.Combine(session, "chat.jsonl"), TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task EveryConcurrentDisposeWaitsForTheWriterToDrain()
    {
        using var owned = new OwnedDirectory();
        var invalid = System.IO.Path.Combine(owned.Path, "file");
        await File.WriteAllTextAsync(invalid, "not a directory", TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ChatLogWriter(invalid);
        writer.StatusChanged += (_, e) =>
        {
            if (e.State == ChatLogWriterState.WriteFailed)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        Task? first = null;
        try
        {
            Assert.True(writer.Enqueue(Message("one")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            first = writer.DisposeAsync().AsTask();
            var second = writer.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(writer.Enqueue(Message("late")));
        }
        finally
        {
            release.TrySetResult();
            if (first is not null)
                await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await writer.DisposeAsync();
        }
    }

    private static ChatMessage Message(string id) => new()
    {
        Id = id, Channel = "audit", BroadcasterId = "broadcaster", UserLogin = "viewer",
        DisplayName = "Viewer", Text = id, Timestamp = DateTimeOffset.UtcNow
    };

    private static string SessionDirectory(string root, ChatMessage message) =>
        System.IO.Path.Combine(root, message.Channel,
            message.Timestamp.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_chat");

    private sealed class OwnedDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "WitherChat-log-audit-" + Guid.NewGuid().ToString("N"));
        public OwnedDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary path escaped its root.");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
