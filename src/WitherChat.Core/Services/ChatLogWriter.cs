using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Channels;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class ChatLogWriter : IAsyncDisposable
{
    private const int MaximumQueuedMessages = 5_000;
    private const int MaximumWriteBatchSize = 250;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private string _logDirectory;
    private volatile bool _enabled = true;
    private volatile bool _saveText = true;
    private volatile bool _logBadges = true;
    private volatile bool _logChannelPoints = true;
    private readonly Channel<ChatMessage> _queue = Channel.CreateBounded<ChatMessage>(
        new BoundedChannelOptions(MaximumQueuedMessages)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Task _writerTask;
    private long _droppedMessageCount;
    private int _writeFailureActive;
    private int _disposed;

    public ChatLogWriter(string logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("A log directory is required.", nameof(logDirectory));
        }

        _logDirectory = Path.GetFullPath(logDirectory);
        _writerTask = RunWriterAsync();
    }

    public string LogDirectory => _logDirectory;
    public long DroppedMessageCount => Interlocked.Read(ref _droppedMessageCount);

    public event EventHandler<ChatLogWriterStatusChangedEventArgs>? StatusChanged;

    public void Configure(
        bool enabled,
        string? logDirectory = null,
        bool saveText = true,
        bool logBadges = true,
        bool logChannelPoints = true)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            _logDirectory = Path.GetFullPath(logDirectory.Trim());
        }
        _enabled = enabled;
        _saveText = saveText;
        _logBadges = logBadges;
        _logChannelPoints = logChannelPoints;
    }

    public bool Enqueue(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_enabled || (!_logChannelPoints && message.IsChannelPointRedemption) ||
            Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (_queue.Writer.TryWrite(message))
        {
            return true;
        }

        var dropped = Interlocked.Increment(ref _droppedMessageCount);
        if (dropped == 1 || (dropped & (dropped - 1)) == 0)
        {
            RaiseStatusChanged(new ChatLogWriterStatusChangedEventArgs(
                ChatLogWriterState.QueueOverflow,
                dropped));
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Chat logging is secondary and must never terminate the application or stop later batches.")]
    private async Task RunWriterAsync()
    {
        var batch = new List<ChatMessage>(MaximumWriteBatchSize);
        await foreach (var message in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            batch.Add(message);
            while (batch.Count < MaximumWriteBatchSize && _queue.Reader.TryRead(out var queued))
            {
                batch.Add(queued);
            }

            try
            {
                foreach (var group in batch.GroupBy(GetSessionDirectory, StringComparer.OrdinalIgnoreCase))
                {
                    var directory = Path.Combine(_logDirectory, group.Key);
                    Directory.CreateDirectory(directory);
                    RestrictDirectoryToCurrentUser(directory);
                    var messages = group.ToArray();
                    if (_saveText)
                    {
                        var textPath = Path.Combine(directory, "chat.txt");
                        await File.AppendAllLinesAsync(
                                textPath,
                                messages.Select(FormatMessage))
                            .ConfigureAwait(false);
                        RestrictFileToCurrentUser(textPath);
                    }
                    var jsonPath = Path.Combine(directory, "chat.jsonl");
                    await File.AppendAllLinesAsync(
                            jsonPath,
                            messages.Select(message => FormatJsonMessage(message, _logBadges)))
                        .ConfigureAwait(false);
                    RestrictFileToCurrentUser(jsonPath);
                    await UpdateMetadataAsync(directory, messages).ConfigureAwait(false);
                }

                if (Interlocked.Exchange(ref _writeFailureActive, 0) != 0)
                {
                    RaiseStatusChanged(new ChatLogWriterStatusChangedEventArgs(
                        ChatLogWriterState.Recovered,
                        DroppedMessageCount));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A failed append may have written only part of a batch. Re-enqueuing it
                // could duplicate chat lines, so the failure is reported explicitly and
                // subsequent messages are allowed to verify that the destination recovered.
                if (Interlocked.Exchange(ref _writeFailureActive, 1) == 0)
                {
                    RaiseStatusChanged(new ChatLogWriterStatusChangedEventArgs(
                        ChatLogWriterState.WriteFailed,
                        DroppedMessageCount,
                        exception));
                }
            }
            finally
            {
                batch.Clear();
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A diagnostic subscriber must never terminate the background log writer.")]
    private void RaiseStatusChanged(ChatLogWriterStatusChangedEventArgs eventArgs)
    {
        try
        {
            StatusChanged?.Invoke(this, eventArgs);
        }
        catch (Exception)
        {
        }
    }

    private static string GetSessionDirectory(ChatMessage message)
    {
        var channel = string.Concat(message.Channel.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_'));
        if (channel.Length == 0)
        {
            channel = "chat";
        }

        return Path.Combine(
            channel,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{message.Timestamp.LocalDateTime:yyyy-MM-dd}_chat"));
    }

    private static async Task UpdateMetadataAsync(string directory, IReadOnlyList<ChatMessage> messages)
    {
        var path = Path.Combine(directory, "metadata.json");
        SessionMetadata? metadata = null;
        if (File.Exists(path))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<SessionMetadata>(await File.ReadAllTextAsync(path)
                    .ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                metadata = null;
            }
        }

        var first = messages[0];
        metadata ??= new SessionMetadata
        {
            LogMode = "daily",
            ChannelLogin = first.Channel,
            ChannelDisplayName = first.Channel,
            BroadcasterId = first.BroadcasterId,
            LogStartedAtLocal = first.Timestamp.ToLocalTime(),
            LogStartedAtUtc = first.Timestamp.ToUniversalTime(),
            AppVersion = AppVersion.Current
        };
        if (metadata.BroadcasterId.Length == 0)
        {
            metadata.BroadcasterId = first.BroadcasterId;
        }
        metadata.MessageCount += messages.Count;
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata, MetadataJsonOptions))
            .ConfigureAwait(false);
        RestrictFileToCurrentUser(temporaryPath);
        File.Move(temporaryPath, path, true);
    }

    private static void RestrictDirectoryToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFileToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string FormatMessage(ChatMessage message)
    {
        var user = message.UserLabel.Replace('\r', ' ').Replace('\n', ' ');
        var text = message.Text.Replace('\r', ' ').Replace('\n', ' ');
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{message.Timestamp.UtcDateTime:O}] {user}: {text}");
    }

    private static string FormatJsonMessage(ChatMessage message, bool includeBadges) =>
        JsonSerializer.Serialize(new
        {
            id = message.Id,
            timestamp = message.Timestamp,
            channel = message.Channel,
            broadcasterId = message.BroadcasterId,
            userId = message.UserId,
            userLogin = message.UserLogin,
            displayName = message.DisplayName,
            text = message.Text,
            color = message.UserColor,
            badges = includeBadges ? message.Badges : Array.Empty<ChatBadge>(),
            parts = message.Parts,
            customRewardId = message.CustomRewardId,
            rewardTitle = message.RewardTitle,
            rewardCost = message.RewardCost,
            rewardPrompt = message.RewardPrompt,
            sourceBroadcasterId = message.SourceBroadcasterId,
            sourceChannelLogin = message.SourceChannelLogin,
            sourceChannelDisplayName = message.SourceChannelDisplayName,
            platform = message.Platform,
            userProfileImageUrl = message.UserProfileImageUri?.AbsoluteUri,
            isAction = message.IsAction,
            isPinned = message.IsPinned
        });

    private sealed class SessionMetadata
    {
        public string LogMode { get; set; } = string.Empty;
        public string ChannelLogin { get; set; } = string.Empty;
        public string ChannelDisplayName { get; set; } = string.Empty;
        public string BroadcasterId { get; set; } = string.Empty;
        public string StreamTitle { get; set; } = string.Empty;
        public string GameName { get; set; } = string.Empty;
        public DateTimeOffset? StreamStartedAtUtc { get; set; }
        public DateTimeOffset LogStartedAtLocal { get; set; }
        public DateTimeOffset LogStartedAtUtc { get; set; }
        public bool IsLive { get; set; }
        public string AppVersion { get; set; } = string.Empty;
        public long MessageCount { get; set; }
    }
}

public enum ChatLogWriterState
{
    QueueOverflow,
    WriteFailed,
    Recovered
}

public sealed class ChatLogWriterStatusChangedEventArgs(
    ChatLogWriterState state,
    long droppedMessageCount,
    Exception? error = null) : EventArgs
{
    public ChatLogWriterState State { get; } = state;
    public long DroppedMessageCount { get; } = droppedMessageCount;
    public Exception? Error { get; } = error;
}
