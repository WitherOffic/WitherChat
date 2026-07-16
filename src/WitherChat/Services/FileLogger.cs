using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace WitherChat.Services;

public sealed class FileLogger
{
    private const long MaxLogBytes = 512 * 1024;
    private const int MaxQueuedEntries = 4096;
    private static readonly TimeSpan DropSummaryInterval = TimeSpan.FromSeconds(30);
    private static readonly Regex SensitiveValuePattern = new(
        @"(?ix)(?:\b(?:access_token|refresh_token|client_secret)\b\s*(?:=|:|%3[dD])\s*|\b(?:bearer|oauth:)\s+?)[^\s&\""']+|\bauthorization\b\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Channel<LogEntry> Queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(MaxQueuedEntries)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private static readonly Task WriterTask = Task.Run(ProcessQueueAsync);
    private static long _droppedEntries;
    private static int _shutdownStarted;

    public void Info(string message) => QueueWrite("INFO", message);
    public void Warn(string message) => QueueWrite("WARN", message);
    public void Error(string message, Exception? exception = null) =>
        QueueWrite("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    public static async Task FlushAsync()
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            await WriterTask.ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Queue.Writer.WriteAsync(LogEntry.Flush(completion)).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            await WriterTask.ConfigureAwait(false);
            return;
        }
        await completion.Task.ConfigureAwait(false);
    }

    public static async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            Queue.Writer.TryComplete();
        }

        await WriterTask.ConfigureAwait(false);
    }

    private static void QueueWrite(string level, string message)
    {
        if (Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        if (!Queue.Writer.TryWrite(new LogEntry(level, message ?? string.Empty, null)))
        {
            Interlocked.Increment(ref _droppedEntries);
        }
    }

    private static async Task ProcessQueueAsync()
    {
        var lastDropSummaryTimestamp = Stopwatch.GetTimestamp();
        await foreach (var entry in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var now = Stopwatch.GetTimestamp();
            var shouldReportDrops = entry.Completion is not null ||
                                    (Volatile.Read(ref _droppedEntries) > 0 &&
                                     Stopwatch.GetElapsedTime(lastDropSummaryTimestamp, now) >= DropSummaryInterval);
            if (shouldReportDrops)
            {
                var dropped = Interlocked.Exchange(ref _droppedEntries, 0);
                if (dropped > 0)
                {
                    await WriteCoreAsync("WARN", $"Application log queue dropped {dropped} entries.").ConfigureAwait(false);
                }

                lastDropSummaryTimestamp = now;
            }

            if (entry.Completion is not null)
            {
                entry.Completion.TrySetResult(true);
                continue;
            }

            await WriteCoreAsync(entry.Level, entry.Message).ConfigureAwait(false);
        }

        var finalDropped = Interlocked.Exchange(ref _droppedEntries, 0);
        if (finalDropped > 0)
        {
            await WriteCoreAsync("WARN", $"Application log queue dropped {finalDropped} entries during shutdown.").ConfigureAwait(false);
        }
    }

    private static async Task WriteCoreAsync(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            RotateIfNeeded();
            var safeMessage = SensitiveValuePattern.Replace(message, "<redacted>");
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {safeMessage}{Environment.NewLine}";
            await File.AppendAllTextAsync(AppPaths.LogFile, line, Encoding.UTF8).ConfigureAwait(false);
        }
        catch
        {
            // Logging must never break chat, auth or moderation.
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (!file.Exists || file.Length < MaxLogBytes)
        {
            return;
        }

        var archived = Path.Combine(AppPaths.LogDirectory, "app.1.log");
        if (File.Exists(archived))
        {
            File.Delete(archived);
        }

        File.Move(AppPaths.LogFile, archived);
    }

    private sealed record LogEntry(string Level, string Message, TaskCompletionSource<bool>? Completion)
    {
        public static LogEntry Flush(TaskCompletionSource<bool> completion) => new(string.Empty, string.Empty, completion);
    }
}
