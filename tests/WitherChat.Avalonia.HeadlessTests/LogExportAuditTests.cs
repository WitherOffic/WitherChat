using System.Text;
using System.Text.Json;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class LogExportAuditTests
{
    [Theory]
    [InlineData("selected", false)]
    [InlineData("selected", true)]
    [InlineData("json", false)]
    [InlineData("txt", true)]
    [InlineData("relative", true)]
    [InlineData("case", false)]
    public async Task OriginalAndCompanionLogsCannotBeExportDestinations(string kind, bool jsonLines)
    {
        using var files = new ExportFiles();
        var target = kind switch
        {
            "json" => Path.Combine(files.DirectoryPath, "chat.jsonl"),
            "txt" => Path.Combine(files.DirectoryPath, "chat.txt"),
            "relative" => Path.Combine(files.DirectoryPath, "unused", "..", "viewer.log"),
            "case" when OperatingSystem.IsWindows() => files.Source.ToUpperInvariant(),
            _ => files.Source
        };
        var opened = false;
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            MainWindow.ExportLogToStreamAsync(files.Source, target, () =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream());
            }, jsonLines, "collision"));
        Assert.Equal("collision", exception.Message);
        Assert.False(opened);
        files.AssertUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportCopiesRequestedCompanionWithoutChangingLogs(bool jsonLines)
    {
        using var files = new ExportFiles();
        var output = new MemoryStream();
        output.Write(Encoding.UTF8.GetBytes("previous destination contents"));
        await MainWindow.ExportLogToStreamAsync(files.Source,
            Path.Combine(files.DirectoryPath, "export.txt"),
            () => Task.FromResult<Stream>(output), jsonLines);
        Assert.Equal(Encoding.UTF8.GetBytes(jsonLines ? ExportFiles.Json : ExportFiles.Text), output.ToArray());
        files.AssertUnchanged();
    }

    [Fact]
    public async Task LegacyLogConvertsToJsonWithoutLosingText()
    {
        using var files = new ExportFiles(companions: false);
        var output = new MemoryStream();
        await MainWindow.ExportLogToStreamAsync(files.Source, null,
            () => Task.FromResult<Stream>(output), jsonLines: true);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(output.ToArray()).Trim());
        Assert.Equal("Viewer", document.RootElement.GetProperty("user").GetString());
        Assert.Equal("Привет 👋", document.RootElement.GetProperty("text").GetString());
        Assert.Equal(ExportFiles.Legacy.TrimEnd(), document.RootElement.GetProperty("raw").GetString());
        files.AssertUnchanged();
    }

    [Fact]
    public async Task MissingSourceDoesNotOpenOrTruncateDestination()
    {
        using var files = new ExportFiles(companions: false);
        var opened = false;
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            MainWindow.ExportLogToStreamAsync(Path.Combine(files.DirectoryPath, "missing.log"), null, () =>
            {
                opened = true;
                return Task.FromResult<Stream>(new MemoryStream());
            }, jsonLines: false));
        Assert.False(opened);
        files.AssertUnchanged();
    }

    [Fact]
    public async Task OutputOpenFailureLeavesSourceUntouched()
    {
        using var files = new ExportFiles();
        await Assert.ThrowsAsync<IOException>(() =>
            MainWindow.ExportLogToStreamAsync(files.Source, null,
                () => Task.FromException<Stream>(new IOException("write denied")), jsonLines: false));
        files.AssertUnchanged();
    }

    [Fact]
    public async Task UnsupportedOutputLeavesSourceUntouched()
    {
        using var files = new ExportFiles();
        var output = new UnsupportedOutput();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            MainWindow.ExportLogToStreamAsync(files.Source, null,
                () => Task.FromResult<Stream>(output), jsonLines: false));
        Assert.True(output.Disposed);
        files.AssertUnchanged();
    }

    [Fact]
    public async Task ActiveLogWriterDoesNotBlockExport()
    {
        using var files = new ExportFiles(companions: false);
        await using var writer = new FileStream(files.Source, FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite);
        var output = new MemoryStream();
        await MainWindow.ExportLogToStreamAsync(files.Source, null,
            () => Task.FromResult<Stream>(output), jsonLines: false);
        Assert.Equal(Encoding.UTF8.GetBytes(ExportFiles.Legacy), output.ToArray());
        await writer.DisposeAsync();
        files.AssertUnchanged();
    }

    private sealed class UnsupportedOutput : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ExportFiles : IDisposable
    {
        public const string Legacy = "[12:34:56] Viewer: Привет 👋\n";
        public const string Json = "{\"text\":\"Привет 👋\"}\n";
        public const string Text = "Viewer: Привет 👋\n";
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "WitherChat-export-audit-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(DirectoryPath, "viewer.log");
        private readonly bool _companions;

        public ExportFiles(bool companions = true)
        {
            _companions = companions;
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Source, Legacy);
            if (companions)
            {
                File.WriteAllText(Path.Combine(DirectoryPath, "chat.jsonl"), Json);
                File.WriteAllText(Path.Combine(DirectoryPath, "chat.txt"), Text);
            }
        }

        public void AssertUnchanged()
        {
            Assert.Equal(Legacy, File.ReadAllText(Source));
            if (_companions)
            {
                Assert.Equal(Json, File.ReadAllText(Path.Combine(DirectoryPath, "chat.jsonl")));
                Assert.Equal(Text, File.ReadAllText(Path.Combine(DirectoryPath, "chat.txt")));
            }
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
