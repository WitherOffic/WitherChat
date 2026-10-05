using System.Globalization;
using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class ReservedChannelUserAuditTests
{
    [Theory]
    [InlineData("con")]
    [InlineData("AUX")]
    [InlineData("nul")]
    [InlineData("prn")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("console")]
    [InlineData("com10")]
    public async Task ValidChannelLoginsNeverBecomeWindowsDevicePaths(string login)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WitherChat-reserved-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var message = new ChatMessage
            {
                Id = "one", Channel = login, UserLogin = "viewer", DisplayName = "Viewer",
                Text = "hello", Timestamp = DateTimeOffset.UtcNow, BroadcasterId = "channel-id"
            };
            var failed = false;
            var writer = new ChatLogWriter(directory);
            writer.StatusChanged += (_, status) => failed |= status.State == ChatLogWriterState.WriteFailed;
            Assert.True(writer.Enqueue(message));
            await writer.DisposeAsync();
            Assert.False(failed, "A valid channel must not fail logging because its name is a Windows device.");
            var jsonPath = Assert.Single(Directory.EnumerateFiles(directory, "chat.jsonl", SearchOption.AllDirectories));
            using var entry = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(jsonPath,
                TestContext.Current.CancellationToken)));
            Assert.Equal(login, entry.RootElement.GetProperty("channel").GetString());
            using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(jsonPath)!, "metadata.json"), TestContext.Current.CancellationToken));
            Assert.Equal(login, metadata.RootElement.GetProperty("channelLogin").GetString());
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
