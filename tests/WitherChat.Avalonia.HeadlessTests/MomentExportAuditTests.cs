using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class MomentExportAuditTests
{
    private static readonly StreamMoment Moment = new("id", ChatPlatforms.Twitch, "audit",
        "message", "Viewer", "Привет 👋", "note", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("stream-moments.json")]
    [InlineData("settings.json")]
    [InlineData("twitch-session.dat")]
    [InlineData("chat_logs/chat.jsonl")]
    [InlineData("unused/../stream-moments.json")]
    [InlineData("")]
    public async Task ExportCannotWriteInsideApplicationData(string relativePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "WitherChat-export-audit-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, relativePath);
        if (OperatingSystem.IsWindows()) target = target.ToUpperInvariant();
        var opened = false;
        var error = await Assert.ThrowsAsync<IOException>(() =>
            MainWindow.ExportMomentsToStreamAsync([Moment], root, target, () =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream());
            }, "protected"));
        Assert.Equal("protected", error.Message);
        Assert.False(opened);
    }

    [Fact]
    public async Task SiblingFolderWithSimilarPrefixRemainsValidAndOldOutputIsReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "WitherChat-export-audit-" + Guid.NewGuid().ToString("N"));
        var output = new MemoryStream();
        output.Write(Encoding.UTF8.GetBytes(new string('x', 2000)));
        await MainWindow.ExportMomentsToStreamAsync([Moment], root, root + "-exports/moments.json",
            () => Task.FromResult<Stream>(output));
        var values = JsonSerializer.Deserialize<StreamMoment[]>(output.ToArray(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(Moment, Assert.Single(values!));
    }

    [Fact]
    public async Task OutputOpenFailureDoesNotMutateSnapshot()
    {
        var snapshot = new[] { Moment };
        await Assert.ThrowsAsync<IOException>(() => MainWindow.ExportMomentsToStreamAsync(snapshot, null, null,
            () => Task.FromException<Stream>(new IOException("write denied"))));
        Assert.Equal(Moment, Assert.Single(snapshot));
    }
}
