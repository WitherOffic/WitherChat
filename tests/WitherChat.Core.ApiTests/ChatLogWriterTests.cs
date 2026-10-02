using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class ChatLogWriterTests
{
    [Fact]
    public async Task StructuredLogPreservesBadgesAndRichMessageParts()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WitherChat-log-test-" + Guid.NewGuid().ToString("N"));
        var writer = new ChatLogWriter(directory);
        try
        {
            writer.Enqueue(new ChatMessage
            {
                Id = "message-1",
                Channel = "channel",
                BroadcasterId = "100",
                UserId = "42",
                UserLogin = "tester",
                DisplayName = "Tester",
                Text = "Kappa hello",
                Timestamp = DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
                UserColor = "#00FF7F",
                Badges =
                [
                    new ChatBadge(
                        "moderator",
                        "1",
                        ImageUri: new Uri("https://static-cdn.jtvnw.net/badges/v1/mod/2"),
                        Title: "Moderator")
                ],
                Parts =
                [
                    ChatMessagePart.TwitchEmote("Kappa", "25"),
                    ChatMessagePart.PlainText(" hello")
                ]
            });
            await writer.DisposeAsync();

            var path = Directory
                .EnumerateFiles(directory, "chat.jsonl", SearchOption.AllDirectories)
                .Single();
            using var document = JsonDocument.Parse(
                (await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken)).Single());
            var root = document.RootElement;
            Assert.Equal("moderator", root.GetProperty("badges")[0].GetProperty("SetId").GetString());
            Assert.Contains(
                "static-cdn.jtvnw.net",
                root.GetProperty("badges")[0].GetProperty("ImageUri").GetString());
            Assert.Equal(2, root.GetProperty("parts").GetArrayLength());
            Assert.Equal("25", root.GetProperty("parts")[0].GetProperty("EmoteId").GetString());
            Assert.Equal("Twitch", root.GetProperty("parts")[0].GetProperty("Provider").GetString());
        }
        finally
        {
            await writer.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteFailureIsReportedOnceAndAHealthyDestinationReportsRecovery()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "WitherChat-log-recovery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var invalidDestination = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(
            invalidDestination,
            "occupied by a file",
            TestContext.Current.CancellationToken);
        var healthyDestination = Path.Combine(root, "healthy");
        var failure = new TaskCompletionSource<ChatLogWriterStatusChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new TaskCompletionSource<ChatLogWriterStatusChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ChatLogWriter(invalidDestination);
        writer.StatusChanged += (_, eventArgs) =>
        {
            if (eventArgs.State == ChatLogWriterState.WriteFailed)
            {
                failure.TrySetResult(eventArgs);
            }
            else if (eventArgs.State == ChatLogWriterState.Recovered)
            {
                recovery.TrySetResult(eventArgs);
            }
        };

        try
        {
            Assert.True(writer.Enqueue(CreateMessage("failed")));
            var failedStatus = await failure.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.IsType<IOException>(failedStatus.Error);

            writer.Configure(enabled: true, healthyDestination);
            Assert.True(writer.Enqueue(CreateMessage("recovered")));
            var recoveredStatus = await recovery.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(ChatLogWriterState.Recovered, recoveredStatus.State);
        }
        finally
        {
            await writer.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnqueueAfterDisposeIsRejectedWithoutThrowing()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WitherChat-log-dispose-test-" + Guid.NewGuid().ToString("N"));
        var writer = new ChatLogWriter(directory);
        await writer.DisposeAsync();

        Assert.False(writer.Enqueue(CreateMessage("late")));
        Assert.False(Directory.Exists(directory));
    }

    private static ChatMessage CreateMessage(string id) => new()
    {
        Id = id,
        Channel = "channel",
        BroadcasterId = "100",
        UserId = "42",
        UserLogin = "tester",
        DisplayName = "Tester",
        Text = id,
        Timestamp = DateTimeOffset.Parse("2026-08-24T00:00:00Z")
    };
}
