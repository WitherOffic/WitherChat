using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WitherChat.Core.Services;

namespace WitherChat.Desktop.Services;

internal static partial class AppDiagnostics
{
    private const long MaximumLogLength = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static string? _logFile;
    private static bool _initialized;

    public static void Initialize(string? dataDirectory = null)
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                var paths = new AppDataPaths(dataDirectory);
                paths.EnsureCreated();
                _logFile = Path.Combine(paths.ConfigDirectory, "diagnostics.log");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException or
                                              System.Security.SecurityException)
            {
                // Diagnostics must never prevent the application from starting.
                _logFile = null;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                {
                    Write("AppDomain.UnhandledException", exception);
                }
                else
                {
                    Write("AppDomain.UnhandledException", eventArgs.ExceptionObject?.ToString() ?? "Unknown error");
                }
            };
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                Write("TaskScheduler.UnobservedTaskException", eventArgs.Exception);
                eventArgs.SetObserved();
            };
            _initialized = true;
        }
    }

    public static void Write(string source, Exception exception) =>
        Write(source, exception.ToString());

    public static void Write(string source, string details)
    {
        var logFile = _logFile;
        if (string.IsNullOrWhiteSpace(logFile))
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                RotateIfNeeded(logFile);
                var line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{DateTimeOffset.UtcNow:O}] {Sanitize(source)}{Environment.NewLine}{Sanitize(details)}{Environment.NewLine}{Environment.NewLine}");
                File.AppendAllText(logFile, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(logFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException or
                                          System.Security.SecurityException)
        {
            // A logging failure must not recursively fail application code.
        }
    }

    internal static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = AuthorizationHeaderRegex().Replace(value, "$1[redacted]");
        sanitized = BearerTokenRegex().Replace(sanitized, "$1[redacted]");
        sanitized = NamedSecretRegex().Replace(sanitized, "$1$2[redacted]");
        return sanitized;
    }

    public static string GetUserMessage(Exception exception, string source = "Handled exception")
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(source, exception);
        return GetUserMessage(exception.Message);
    }

    public static string GetUserMessage(string? details)
    {
        var message = Sanitize(details)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        while (message.Contains("  ", StringComparison.Ordinal))
        {
            message = message.Replace("  ", " ", StringComparison.Ordinal);
        }
        return message.Length <= 320 ? message : message[..317] + "...";
    }

    private static void RotateIfNeeded(string logFile)
    {
        var file = new FileInfo(logFile);
        if (!file.Exists || file.Length < MaximumLogLength)
        {
            return;
        }

        var previous = logFile + ".previous";
        File.Move(logFile, previous, overwrite: true);
    }

    [GeneratedRegex("(?i)(Bearer\\s+)[A-Za-z0-9._~+\\-/=]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(
        "(?im)(authorization\\s*:\\s*)[^\\r\\n]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(
        "(?i)(access[_-]?token|refresh[_-]?token|client[_-]?secret|authorization|api[_-]?key|widget[_-]?token)(\\s*[\"']?\\s*[:=]\\s*[\"']?)([^&\\s,\"'}]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretRegex();
}
