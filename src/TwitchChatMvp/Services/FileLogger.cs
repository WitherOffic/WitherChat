using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TwitchChatMvp.Services;

public sealed class FileLogger
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly Regex SensitiveValuePattern = new(
        @"(?ix)(?:\b(?:access_token|refresh_token)\b\s*(?:=|:|%3[dD])\s*|\b(?:bearer|oauth:)\s+?)[^\s&\""']+|\bauthorization\b\s*[:=]\s*[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Info(string message) => _ = WriteAsync("INFO", message);
    public void Warn(string message) => _ = WriteAsync("WARN", message);
    public void Error(string message, Exception? exception = null) =>
        _ = WriteAsync("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

    private async Task WriteAsync(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                RotateIfNeeded();
                var safeMessage = SensitiveValuePattern.Replace(message ?? string.Empty, "<redacted>");
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {safeMessage}{Environment.NewLine}";
                await File.AppendAllTextAsync(AppPaths.LogFile, line, Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
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
}
