using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class ChatLogService : IAsyncDisposable
{
    private const int MaxQueuedMessages = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FileLogger _logger;
    private readonly Channel<QueuedLogMessage> _queue;
    private readonly Task _writerTask;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly object _sessionSync = new();
    private readonly ConcurrentDictionary<string, long> _messageCounts = new(StringComparer.OrdinalIgnoreCase);
    private SessionState? _session;
    private Task<SessionState?>? _sessionCreationTask;
    private DateTimeOffset _lastWriteWarningAt = DateTimeOffset.MinValue;

    public ChatLogService(FileLogger logger)
    {
        _logger = logger;
        _queue = Channel.CreateBounded<QueuedLogMessage>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _writerTask = Task.Run(ProcessQueueAsync);
        CleanupEmptySessions(AppPaths.ChatLogsDirectory);
    }

    public event EventHandler? WriteFailed;

    public static string GetRootFolder(AppSettings settings)
    {
        settings.Normalize();
        return string.IsNullOrWhiteSpace(settings.ChatLogsFolder)
            ? AppPaths.ChatLogsDirectory
            : Environment.ExpandEnvironmentVariables(settings.ChatLogsFolder.Trim());
    }

    public static IReadOnlyList<ChatLogChannelSummary> GetChannels(AppSettings settings)
    {
        CleanupEmptySessions(settings);
        var root = GetRootFolder(settings);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Select(path => new ChatLogChannelSummary
            {
                DirectoryPath = path,
                Login = Path.GetFileName(path)
            })
            .OrderBy(channel => channel.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<ChatLogSessionSummary> GetSessions(ChatLogChannelSummary channel)
    {
        if (!Directory.Exists(channel.DirectoryPath))
        {
            return [];
        }

        return Directory.EnumerateDirectories(channel.DirectoryPath)
            .Select(CreateSessionSummary)
            .OrderByDescending(session => session.Metadata.StreamStartedAtUtc ?? session.Metadata.LogStartedAtUtc)
            .ToList();
    }

    public static Task<IReadOnlyList<ChatLogMessageEntry>> LoadMessagesAsync(
        ChatLogSessionSummary session,
        string searchText,
        string userFilter,
        string roleFilter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ChatLogMessageEntry>>(() =>
        {
            var path = Path.Combine(session.DirectoryPath, "chat.jsonl");
            if (!File.Exists(path))
            {
                return [];
            }

            limit = Math.Clamp(limit, 100, 50000);
            searchText = (searchText ?? string.Empty).Trim();
            userFilter = (userFilter ?? string.Empty).Trim().TrimStart('@');
            roleFilter = (roleFilter ?? string.Empty).Trim().ToLowerInvariant();
            var hasFilters = !string.IsNullOrWhiteSpace(searchText) ||
                             !string.IsNullOrWhiteSpace(userFilter) ||
                             !string.IsNullOrWhiteSpace(roleFilter);

            var recent = new Queue<ChatLogMessageEntry>(limit);
            var matches = new List<ChatLogMessageEntry>(limit);

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ChatLogMessageEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<ChatLogMessageEntry>(line, JsonOptions);
                }
                catch
                {
                    continue;
                }

                if (entry is null)
                {
                    continue;
                }

                if (!Matches(entry, searchText, userFilter, roleFilter))
                {
                    continue;
                }

                if (hasFilters)
                {
                    matches.Add(entry);
                    if (matches.Count >= limit)
                    {
                        break;
                    }
                }
                else
                {
                    recent.Enqueue(entry);
                    while (recent.Count > limit)
                    {
                        recent.Dequeue();
                    }
                }
            }

            return hasFilters ? matches : recent.ToList();
        }, cancellationToken);
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static async Task ExportAsync(ChatLogSessionSummary session, string fileName, string destinationPath, CancellationToken cancellationToken = default)
    {
        var source = Path.Combine(session.DirectoryPath, fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(fileName);
        }

        await using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var output = File.Create(destinationPath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    public static void DeleteSession(AppSettings settings, ChatLogSessionSummary session)
    {
        var root = Path.GetFullPath(GetRootFolder(settings));
        var sessionPath = Path.GetFullPath(session.DirectoryPath);
        if (!IsChildPath(root, sessionPath) || ContainsReparsePoint(root, sessionPath))
        {
            throw new InvalidOperationException("The selected log session is outside the configured logs folder.");
        }

        if (Directory.Exists(sessionPath))
        {
            Directory.Delete(sessionPath, recursive: true);
        }
    }

    public async Task StartSessionAsync(
        AppSettings settings,
        TwitchUser broadcaster,
        StreamStatusInfo streamStatus,
        CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!settings.EnableChatLogging || (!settings.SaveChatLogJsonl && !settings.SaveChatLogTxt))
            {
                _session = null;
                return;
            }

            var root = GetRootFolder(settings);
            var channelLogin = string.IsNullOrWhiteSpace(broadcaster.Login)
                ? "unknown-channel"
                : broadcaster.Login.Trim().TrimStart('@').ToLowerInvariant();
            var channelDirectory = Path.Combine(root, SanitizePathSegment(channelLogin));

            CleanupEmptySessions(root);

            SessionState? current;
            lock (_sessionSync)
            {
                current = _session;
            }
            var reuseCurrentSession = ShouldReuseSession(current, channelLogin, streamStatus);
            var metadata = reuseCurrentSession
                ? current!.Metadata
                : CreateMetadata(broadcaster, streamStatus);
            metadata.ChannelLogin = channelLogin;
            metadata.ChannelDisplayName = string.IsNullOrWhiteSpace(broadcaster.DisplayName) ? channelLogin : broadcaster.DisplayName;
            metadata.BroadcasterId = broadcaster.Id;
            metadata.IsLive = streamStatus.IsLive;
            metadata.StreamTitle = CreateStreamTitle(streamStatus);
            metadata.GameName = streamStatus.GameName;
            metadata.StreamStartedAtUtc = streamStatus.StartedAt?.ToUniversalTime();
            metadata.AppVersion = AppInfo.Version;

            var existingDirectory = reuseCurrentSession ? current?.DirectoryPath : null;
            if (string.IsNullOrWhiteSpace(existingDirectory) && settings.AutoSplitLogsByStream && streamStatus.IsLive)
            {
                existingDirectory = FindExistingSessionDirectory(channelDirectory, streamStatus);
            }

            var metadataPath = string.IsNullOrWhiteSpace(existingDirectory)
                ? string.Empty
                : Path.Combine(existingDirectory, "metadata.json");
            if (!string.IsNullOrWhiteSpace(existingDirectory))
            {
                metadata.MessageCount = Math.Max(metadata.MessageCount, CountLines(Path.Combine(existingDirectory, "chat.jsonl")));
                _messageCounts[metadataPath] = metadata.MessageCount;
            }

            var state = new SessionState(
                root,
                channelDirectory,
                existingDirectory ?? string.Empty,
                metadataPath,
                streamStatus.IsLive,
                streamStatus.StartedAt?.ToUniversalTime(),
                metadata,
                settings.SaveChatLogJsonl,
                settings.SaveChatLogTxt,
                settings.LogChatBadges);
            lock (_sessionSync)
            {
                _session = state;
                _sessionCreationTask = null;
            }

            _logger.Info($"Chat log session armed: channel={channelLogin}");
        }
        catch (Exception ex)
        {
            _session = null;
            _logger.Error("Chat log session start failed", ex);
            NotifyWriteFailed();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task StopSessionAsync()
    {
        SessionState? session;
        lock (_sessionSync)
        {
            session = _session;
            _session = null;
        }

        await FlushAsync().ConfigureAwait(false);
        _sessionCreationTask = null;
        CleanupEmptySession(session);
    }

    public async Task UpdateStreamInfoAsync(StreamStatusInfo streamStatus, CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = _session;
            if (session is null)
            {
                return;
            }

            var metadata = string.IsNullOrWhiteSpace(session.MetadataPath)
                ? session.Metadata
                : ReadMetadata(session.MetadataPath) ?? session.Metadata;
            metadata.IsLive = streamStatus.IsLive;
            if (streamStatus.IsLive)
            {
                metadata.StreamTitle = CreateStreamTitle(streamStatus);
                metadata.GameName = streamStatus.GameName;
                metadata.StreamStartedAtUtc = streamStatus.StartedAt?.ToUniversalTime();
            }

            metadata.MessageCount = !string.IsNullOrWhiteSpace(session.MetadataPath) &&
                                    _messageCounts.TryGetValue(session.MetadataPath, out var count)
                ? count
                : metadata.MessageCount;
            var updated = session with
            {
                IsLive = streamStatus.IsLive,
                StreamStartedAtUtc = streamStatus.StartedAt?.ToUniversalTime(),
                Metadata = metadata
            };
            if (!string.IsNullOrWhiteSpace(updated.MetadataPath))
            {
                await WriteMetadataAsync(updated.MetadataPath, metadata, cancellationToken).ConfigureAwait(false);
            }

            lock (_sessionSync)
            {
                _session = updated;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Chat log metadata update failed: {ex.GetType().Name}");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public void Enqueue(ChatMessageModel message)
    {
        if (!IsRealChatMessage(message))
        {
            return;
        }

        SessionState? session;
        Task<SessionState?> sessionTask;
        lock (_sessionSync)
        {
            session = _session;
            if (session is null)
            {
                return;
            }

            sessionTask = !string.IsNullOrWhiteSpace(session.DirectoryPath)
                ? Task.FromResult<SessionState?>(session)
                : _sessionCreationTask ??= CreateSessionInBackgroundAsync(session);
        }

        try
        {
            var entry = CreateEntry(session, message);
            var queued = new QueuedLogMessage(
                sessionTask,
                string.Empty,
                string.Empty,
                session.SaveJsonl ? JsonSerializer.Serialize(entry, JsonOptions) : null,
                session.SaveTxt ? $"[{entry.TimestampLocal.LocalDateTime:HH:mm:ss}] {entry.UserLabel}: {entry.Message}" : null);
            if (!_queue.Writer.TryWrite(queued))
            {
                _logger.Warn("Chat log queue is full; newest message was not queued.");
                NotifyWriteFailed();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Chat log enqueue failed: {ex.GetType().Name}");
            NotifyWriteFailed();
        }
    }

    public async Task FlushAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(QueuedLogMessage.Flush(completion)).ConfigureAwait(false);
            await completion.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Chat log flush failed: {ex.GetType().Name}");
            NotifyWriteFailed();
        }
    }

    public async ValueTask DisposeAsync()
    {
        SessionState? session;
        lock (_sessionSync)
        {
            session = _session;
            _session = null;
        }

        _queue.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
            CleanupEmptySession(session);
        }
        catch
        {
            // Chat logging must never block app shutdown.
        }
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<QueuedLogMessage>(64);
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                var readCount = 0;
                while (readCount < 64 && _queue.Reader.TryRead(out var item))
                {
                    readCount++;
                    if (item.FlushCompletion is not null)
                    {
                        if (batch.Count > 0)
                        {
                            await FlushBatchAsync(batch).ConfigureAwait(false);
                            batch.Clear();
                        }

                        item.FlushCompletion.TrySetResult(true);
                        continue;
                    }

                    var session = item.SessionTask is null
                        ? null
                        : await item.SessionTask.ConfigureAwait(false);
                    if (session is not null)
                    {
                        batch.Add(item with
                        {
                            SessionDirectory = session.DirectoryPath,
                            MetadataPath = session.MetadataPath
                        });
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchAsync(batch).ConfigureAwait(false);
                }
            }

        }
        catch (Exception ex)
        {
            _logger.Error("Chat log writer failed", ex);
            _queue.Writer.TryComplete(ex);
            while (_queue.Reader.TryRead(out var pending))
            {
                pending.FlushCompletion?.TrySetException(ex);
            }
        }
    }

    private async Task FlushBatchAsync(IReadOnlyList<QueuedLogMessage> batch)
    {
        foreach (var group in batch.GroupBy(item => item.SessionDirectory, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(group.Key);
                var jsonLines = group.Select(item => item.JsonLine).Where(line => line is not null).Cast<string>().ToList();
                if (jsonLines.Count > 0)
                {
                    await File.AppendAllLinesAsync(Path.Combine(group.Key, "chat.jsonl"), jsonLines, Encoding.UTF8).ConfigureAwait(false);
                }

                var textLines = group.Select(item => item.TextLine).Where(line => line is not null).Cast<string>().ToList();
                if (textLines.Count > 0)
                {
                    await File.AppendAllLinesAsync(Path.Combine(group.Key, "chat.txt"), textLines, Encoding.UTF8).ConfigureAwait(false);
                }

                var metadataPath = group.First().MetadataPath;
                var written = Math.Max(jsonLines.Count, textLines.Count);
                if (written > 0)
                {
                    var count = _messageCounts.AddOrUpdate(metadataPath, written, (_, current) => current + written);
                    await UpdateMessageCountAsync(metadataPath, count).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Chat log write failed: {ex.GetType().Name}");
                NotifyWriteFailed();
            }
        }
    }

    private void NotifyWriteFailed()
    {
        var now = DateTimeOffset.UtcNow;
        if ((now - _lastWriteWarningAt) < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastWriteWarningAt = now;
        WriteFailed?.Invoke(this, EventArgs.Empty);
    }

    private SessionState? EnsureSessionCreated(SessionState? session)
    {
        if (session is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(session.DirectoryPath) &&
            !string.IsNullOrWhiteSpace(session.MetadataPath))
        {
            return session;
        }

        try
        {
            Directory.CreateDirectory(session.ChannelDirectory);
            var existingDirectory = session.IsLive && session.StreamStartedAtUtc is not null
                ? FindExistingSessionDirectory(session.ChannelDirectory, new StreamStatusInfo(
                    true,
                    0,
                    session.Metadata.StreamTitle,
                    session.Metadata.GameName,
                    session.StreamStartedAtUtc))
                : null;
            var sessionDirectory = existingDirectory ?? CreateSessionDirectory(session.ChannelDirectory, session.Metadata);
            Directory.CreateDirectory(sessionDirectory);

            var metadataPath = Path.Combine(sessionDirectory, "metadata.json");
            var metadata = ReadMetadata(metadataPath) ?? session.Metadata;
            metadata.MessageCount = Math.Max(metadata.MessageCount, CountLines(Path.Combine(sessionDirectory, "chat.jsonl")));
            WriteMetadata(metadataPath, metadata);
            _messageCounts[metadataPath] = metadata.MessageCount;

            _logger.Info($"Chat log session created: channel={metadata.ChannelLogin}, path={sessionDirectory}");
            return session with
            {
                DirectoryPath = sessionDirectory,
                MetadataPath = metadataPath,
                Metadata = metadata
            };
        }
        catch (Exception ex)
        {
            _logger.Warn($"Chat log lazy session create failed: {ex.GetType().Name}");
            NotifyWriteFailed();
            return null;
        }
    }

    private async Task<SessionState?> CreateSessionInBackgroundAsync(SessionState session)
    {
        var created = await Task.Run(() => EnsureSessionCreated(session)).ConfigureAwait(false);
        if (created is null)
        {
            return null;
        }

        lock (_sessionSync)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = created;
            }
        }

        return created;
    }

    private static bool IsRealChatMessage(ChatMessageModel message)
    {
        if (message is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.Text) &&
            string.IsNullOrWhiteSpace(message.Login) &&
            string.IsNullOrWhiteSpace(message.DisplayName) &&
            string.IsNullOrWhiteSpace(message.UserId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(message.Text) ||
               !string.IsNullOrWhiteSpace(message.Login) ||
               !string.IsNullOrWhiteSpace(message.DisplayName);
    }

    private static bool ShouldReuseSession(SessionState? current, string channelLogin, StreamStatusInfo status)
    {
        if (current is null ||
            !string.Equals(current.Metadata.ChannelLogin, channelLogin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!current.IsLive && !status.IsLive)
        {
            return true;
        }

        if (!current.IsLive || !status.IsLive ||
            current.StreamStartedAtUtc is null ||
            status.StartedAt is null)
        {
            return false;
        }

        return Math.Abs((current.StreamStartedAtUtc.Value - status.StartedAt.Value.ToUniversalTime()).TotalSeconds) < 2;
    }

    public static void CleanupEmptySessions(AppSettings settings)
    {
        CleanupEmptySessions(GetRootFolder(settings));
    }

    private static void CleanupEmptySessions(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }

            foreach (var channelDirectory in Directory.EnumerateDirectories(root))
            {
                foreach (var sessionDirectory in Directory.EnumerateDirectories(channelDirectory))
                {
                    if (IsEmptyLogSessionDirectory(sessionDirectory))
                    {
                        Directory.Delete(sessionDirectory, recursive: true);
                    }
                }

                if (!Directory.EnumerateFileSystemEntries(channelDirectory).Any())
                {
                    Directory.Delete(channelDirectory, recursive: false);
                }
            }
        }
        catch
        {
            // Cleanup should be best-effort and never affect chat.
        }
    }

    private static void CleanupEmptySession(SessionState? session)
    {
        if (session is null ||
            string.IsNullOrWhiteSpace(session.DirectoryPath) ||
            !Directory.Exists(session.DirectoryPath) ||
            !IsChildPath(session.RootDirectory, session.DirectoryPath))
        {
            return;
        }

        try
        {
            if (IsEmptyLogSessionDirectory(session.DirectoryPath))
            {
                Directory.Delete(session.DirectoryPath, recursive: true);
            }

            if (Directory.Exists(session.ChannelDirectory) &&
                !Directory.EnumerateFileSystemEntries(session.ChannelDirectory).Any())
            {
                Directory.Delete(session.ChannelDirectory, recursive: false);
            }
        }
        catch
        {
            // Empty log cleanup is intentionally silent.
        }
    }

    private static bool IsEmptyLogSessionDirectory(string sessionDirectory)
    {
        var jsonPath = Path.Combine(sessionDirectory, "chat.jsonl");
        var txtPath = Path.Combine(sessionDirectory, "chat.txt");
        return !HasMessageLines(jsonPath) && !HasMessageLines(txtPath);
    }

    private static bool HasMessageLines(string path)
    {
        try
        {
            return File.Exists(path) &&
                   File.ReadLines(path, Encoding.UTF8).Any(line => !string.IsNullOrWhiteSpace(line));
        }
        catch
        {
            return true;
        }
    }

    private static bool IsChildPath(string parentPath, string childPath)
    {
        var parentPathFull = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childPathFull = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !childPathFull.Equals(parentPathFull, StringComparison.OrdinalIgnoreCase) &&
               childPathFull.StartsWith(parentPathFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsReparsePoint(string rootPath, string childPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(childPath);
        while (current is not null && !current.FullName.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            current = current.Parent;
        }

        return current is null;
    }

    private static ChatLogMessageEntry CreateEntry(SessionState session, ChatMessageModel message)
    {
        var timestampLocal = message.Timestamp.ToLocalTime();
        var timestampUtc = message.Timestamp.ToUniversalTime();
        var badges = session.LogBadges
            ? message.Badges.Select(badge => new ChatLogBadgeEntry
            {
                SetId = badge.SetId,
                Id = badge.Id,
                Info = badge.Info,
                Title = badge.Title
            }).ToList()
            : [];

        return new ChatLogMessageEntry
        {
            TimestampLocal = timestampLocal,
            TimestampUtc = timestampUtc,
            ChannelLogin = session.Metadata.ChannelLogin,
            StreamTitle = session.Metadata.StreamTitle,
            UserId = message.UserId,
            UserLogin = message.Login,
            DisplayName = message.DisplayName,
            UserColor = message.Color,
            Badges = badges,
            Message = message.Text,
            MessageId = message.Id,
            IsModerator = message.IsModerator,
            IsSubscriber = message.IsSubscriber,
            IsBroadcaster = message.Badges.Any(b => string.Equals(b.SetId, "broadcaster", StringComparison.OrdinalIgnoreCase)),
            IsVip = message.IsVip
        };
    }

    private static bool Matches(ChatLogMessageEntry entry, string searchText, string userFilter, string roleFilter)
    {
        if (!string.IsNullOrWhiteSpace(searchText) &&
            !entry.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
            !entry.UserLabel.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
            !entry.UserLogin.Contains(searchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(userFilter) &&
            !entry.UserLogin.Contains(userFilter, StringComparison.OrdinalIgnoreCase) &&
            !entry.UserLabel.Contains(userFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return entry.MatchesRole(roleFilter);
    }

    private static ChatLogSessionSummary CreateSessionSummary(string directoryPath)
    {
        var metadataPath = Path.Combine(directoryPath, "metadata.json");
        var metadata = ReadMetadata(metadataPath) ?? new ChatLogSessionMetadata
        {
            ChannelLogin = Directory.GetParent(directoryPath)?.Name ?? string.Empty,
            StreamTitle = Path.GetFileName(directoryPath),
            LogStartedAtLocal = Directory.GetCreationTime(directoryPath),
            LogStartedAtUtc = Directory.GetCreationTimeUtc(directoryPath),
            AppVersion = AppInfo.Version
        };

        if (metadata.MessageCount <= 0)
        {
            metadata.MessageCount = CountLines(Path.Combine(directoryPath, "chat.jsonl"));
        }

        return new ChatLogSessionSummary
        {
            DirectoryPath = directoryPath,
            Metadata = metadata
        };
    }

    private static string? FindExistingSessionDirectory(string channelDirectory, StreamStatusInfo status)
    {
        if (!status.IsLive || status.StartedAt is null || !Directory.Exists(channelDirectory))
        {
            return null;
        }

        var startedAt = status.StartedAt.Value.ToUniversalTime();
        foreach (var directory in Directory.EnumerateDirectories(channelDirectory))
        {
            var metadata = ReadMetadata(Path.Combine(directory, "metadata.json"));
            if (metadata?.StreamStartedAtUtc is not null &&
                Math.Abs((metadata.StreamStartedAtUtc.Value - startedAt).TotalSeconds) < 2)
            {
                return directory;
            }
        }

        return null;
    }

    private static string CreateSessionDirectory(string channelDirectory, ChatLogSessionMetadata metadata)
    {
        var sessionName = metadata.IsLive
            ? $"{(metadata.StreamStartedAtUtc?.LocalDateTime ?? metadata.LogStartedAtLocal.LocalDateTime):yyyy-MM-dd_HH-mm}_{SanitizePathSegment(metadata.StreamTitle)}"
            : SanitizePathSegment($"offline_chat_{metadata.LogStartedAtLocal.LocalDateTime:yyyy-MM-dd_HH-mm}");
        var directory = Path.Combine(channelDirectory, sessionName);
        if (!Directory.Exists(directory))
        {
            return directory;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(channelDirectory, $"{sessionName}_{i}");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(channelDirectory, $"{sessionName}_{Guid.NewGuid():N}");
    }

    private static ChatLogSessionMetadata CreateMetadata(TwitchUser broadcaster, StreamStatusInfo status)
    {
        var now = DateTimeOffset.Now;
        var channelLogin = string.IsNullOrWhiteSpace(broadcaster.Login)
            ? "unknown-channel"
            : broadcaster.Login.Trim().TrimStart('@').ToLowerInvariant();

        return new ChatLogSessionMetadata
        {
            ChannelLogin = channelLogin,
            ChannelDisplayName = string.IsNullOrWhiteSpace(broadcaster.DisplayName) ? channelLogin : broadcaster.DisplayName,
            BroadcasterId = broadcaster.Id,
            StreamTitle = CreateStreamTitle(status),
            GameName = status.GameName,
            StreamStartedAtUtc = status.StartedAt?.ToUniversalTime(),
            LogStartedAtLocal = now,
            LogStartedAtUtc = now.ToUniversalTime(),
            IsLive = status.IsLive,
            AppVersion = AppInfo.Version
        };
    }

    private static string CreateStreamTitle(StreamStatusInfo status)
    {
        if (status.IsLive)
        {
            return string.IsNullOrWhiteSpace(status.Title) ? "unknown-stream" : status.Title.Trim();
        }

        return $"offline_chat_{DateTime.Now:yyyy-MM-dd_HH-mm}";
    }

    private static async Task UpdateMessageCountAsync(string metadataPath, long count)
    {
        var metadata = ReadMetadata(metadataPath);
        if (metadata is null)
        {
            return;
        }

        metadata.MessageCount = count;
        await WriteMetadataAsync(metadataPath, metadata, CancellationToken.None).ConfigureAwait(false);
    }

    private static ChatLogSessionMetadata? ReadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ChatLogSessionMetadata>(json, MetadataJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(string path, ChatLogSessionMetadata metadata, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteMetadata(string path, ChatLogSessionMetadata metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static long CountLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path, Encoding.UTF8).LongCount() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string SanitizePathSegment(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "unknown-stream" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '-' : ch);
        }

        var result = builder.ToString();
        while (result.Contains("--", StringComparison.Ordinal))
        {
            result = result.Replace("--", "-", StringComparison.Ordinal);
        }

        result = result.Trim(' ', '.', '-');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = "unknown-stream";
        }

        return result.Length > 90 ? result[..90].TrimEnd(' ', '.', '-') : result;
    }

    private sealed record SessionState(
        string RootDirectory,
        string ChannelDirectory,
        string DirectoryPath,
        string MetadataPath,
        bool IsLive,
        DateTimeOffset? StreamStartedAtUtc,
        ChatLogSessionMetadata Metadata,
        bool SaveJsonl,
        bool SaveTxt,
        bool LogBadges);

    private sealed record QueuedLogMessage(
        Task<SessionState?>? SessionTask,
        string SessionDirectory,
        string MetadataPath,
        string? JsonLine,
        string? TextLine,
        TaskCompletionSource<bool>? FlushCompletion = null)
    {
        public static QueuedLogMessage Flush(TaskCompletionSource<bool> completion) =>
            new(null, string.Empty, string.Empty, null, null, completion);
    }
}
