using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class ChatLogService : IAsyncDisposable
{
    private const int MaxQueuedMessages = 10000;
    private const int MaxWriteAttempts = 4;
    private const int MaxLogLineBytes = 1024 * 1024;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ShutdownCancelTimeout = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly object MetadataWriteLocksGate = new();
    private static readonly Dictionary<string, MetadataWriteLockEntry> MetadataWriteLocks =
        new(StringComparer.OrdinalIgnoreCase);
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
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _sessionSync = new();
    private readonly ConcurrentDictionary<string, long> _messageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<SessionState?>> _channelSessions = new(StringComparer.OrdinalIgnoreCase);
    private SessionState? _session;
    private Task<SessionState?>? _sessionCreationTask;
    private long _lastWriteWarningTimestamp;
    private long _droppedQueueMessages;
    private long _lastQueueFullWarningTimestamp;

    public ChatLogService(FileLogger logger)
    {
        _logger = logger;
        AppPaths.TryMigrateLegacyChatLogs();
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
        var fallback = Path.GetFullPath(AppPaths.ChatLogsDirectory);
        if (string.IsNullOrWhiteSpace(settings.ChatLogsFolder))
        {
            return fallback;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(settings.ChatLogsFolder.Trim());
            var normalized = Path.IsPathFullyQualified(expanded)
                ? Path.GetFullPath(expanded)
                : Path.GetFullPath(expanded, AppPaths.LocalDataDirectory);
            normalized = Path.TrimEndingDirectorySeparator(normalized);
            var volumeRoot = Path.GetPathRoot(normalized);
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, volumeRoot, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(normalized))
            {
                return fallback;
            }

            return normalized;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    public static IReadOnlyList<ChatLogChannelSummary> GetChannels(AppSettings settings)
    {
        CleanupEmptySessions(settings);
        var root = GetRootFolder(settings);
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(path => IsChildPath(root, path) && !ContainsReparsePoint(root, path))
                .Select(path => new ChatLogChannelSummary
                {
                    DirectoryPath = path,
                    Login = Path.GetFileName(path)
                })
                .Where(channel => GetSessions(channel).Count > 0)
                .OrderBy(channel => channel.Login, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<ChatLogSessionSummary> GetSessions(ChatLogChannelSummary channel)
    {
        if (!Directory.Exists(channel.DirectoryPath))
        {
            return [];
        }

        try
        {
            if (Directory.GetParent(channel.DirectoryPath) is null)
            {
                return [];
            }

            return Directory.EnumerateDirectories(channel.DirectoryPath)
                .Where(path => IsChildPath(channel.DirectoryPath, path) &&
                               !ContainsReparsePoint(channel.DirectoryPath, path))
                .Select(path => TryCreateSessionSummary(path, channel.Login))
                .Where(session => session is not null)
                .Cast<ChatLogSessionSummary>()
                .OrderByDescending(session =>
                    string.Equals(session.Metadata.LogMode, "daily", StringComparison.OrdinalIgnoreCase)
                        ? session.Metadata.LogStartedAtUtc
                        : session.Metadata.StreamStartedAtUtc ?? session.Metadata.LogStartedAtUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
            var matches = new List<ChatLogMessageEntry>(limit);
            foreach (var line in ReadLinesBackwards(path, cancellationToken))
            {
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

                if (entry is null || !NormalizeLogEntry(entry))
                {
                    continue;
                }

                if (!Matches(entry, searchText, userFilter, roleFilter))
                {
                    continue;
                }

                matches.Add(entry);
                if (matches.Count >= limit)
                {
                    break;
                }
            }

            matches.Reverse();
            return matches;
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

        var sourceFullPath = Path.GetFullPath(source);
        var destinationFullPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourceFullPath, destinationFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The export destination must differ from the source log.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath)
            ?? throw new IOException("The export destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = File.Open(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationFullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A completed export has already moved the temporary file; cleanup is best-effort on failure.
            }
        }
    }

    public async Task DeleteSessionAsync(
        AppSettings settings,
        ChatLogSessionSummary session,
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
        var sessionPath = Path.GetFullPath(session.DirectoryPath);
        SessionState? activeSession;
        lock (_sessionSync)
        {
            activeSession = _session;
        }

        var isActive = HasSameDirectory(activeSession, sessionPath) ||
                       _channelSessions.Values.Any(task =>
                           task.IsCompletedSuccessfully && HasSameDirectory(task.Result, sessionPath));
        if (isActive)
        {
            throw new InvalidOperationException(
                LocalizationService.Get(LocalizationService.CurrentLanguage, "ActiveLogCannotDelete"));
        }

        DeleteSession(settings, session);
    }

    public static void DeleteSession(AppSettings settings, ChatLogSessionSummary session)
    {
        var root = Path.GetFullPath(GetRootFolder(settings));
        var sessionPath = Path.GetFullPath(session.DirectoryPath);
        if (!IsOwnedLogSessionDirectory(root, sessionPath))
        {
            throw new InvalidOperationException(
                LocalizationService.Get(LocalizationService.CurrentLanguage, "InvalidLogSession"));
        }

        DeleteOwnedLogSessionDirectory(sessionPath);
    }

    public async Task StartSessionAsync(
        AppSettings settings,
        TwitchUser broadcaster,
        StreamStatusInfo streamStatus,
        CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionState? current;
            lock (_sessionSync)
            {
                current = _session;
            }

            if (!settings.EnableChatLogging)
            {
                _session = null;
                CleanupEmptySession(current);
                return;
            }

            var root = GetRootFolder(settings);
            var channelLogin = string.IsNullOrWhiteSpace(broadcaster.Login)
                ? "unknown-channel"
                : broadcaster.Login.Trim().TrimStart('@').ToLowerInvariant();
            var channelDirectory = Path.Combine(root, SanitizePathSegment(channelLogin));

            CleanupEmptySessions(root);

            var reuseCurrentSession = current is not null &&
                                      ShouldReuseSession(current, channelLogin, streamStatus) &&
                                      string.Equals(Path.GetFullPath(current.RootDirectory), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(Path.GetFullPath(current.ChannelDirectory), Path.GetFullPath(channelDirectory), StringComparison.OrdinalIgnoreCase) &&
                                      current.SaveTxt == settings.SaveChatLogTxt &&
                                      current.LogBadges == settings.LogChatBadges;
            var metadata = reuseCurrentSession
                ? current!.Metadata
                : CreateMetadata(broadcaster, streamStatus);
            metadata.ChannelLogin = channelLogin;
            metadata.ChannelDisplayName = string.IsNullOrWhiteSpace(broadcaster.DisplayName) ? channelLogin : broadcaster.DisplayName;
            metadata.BroadcasterId = broadcaster.Id;
            metadata.LogMode = "daily";
            metadata.IsLive = streamStatus.IsLive;
            if (streamStatus.IsLive)
            {
                metadata.StreamTitle = CreateStreamTitle(streamStatus);
                metadata.GameName = streamStatus.GameName;
                metadata.StreamStartedAtUtc = streamStatus.StartedAt?.ToUniversalTime();
            }
            metadata.AppVersion = AppInfo.Version;

            var existingDirectory = reuseCurrentSession ? current?.DirectoryPath : null;
            if (string.IsNullOrWhiteSpace(existingDirectory))
            {
                existingDirectory = FindDailySessionDirectory(channelDirectory, metadata.LogStartedAtLocal);
            }

            var metadataPath = string.IsNullOrWhiteSpace(existingDirectory)
                ? string.Empty
                : Path.Combine(existingDirectory, "metadata.json");
            if (!string.IsNullOrWhiteSpace(existingDirectory))
            {
                var storedMetadata = ReadMetadata(metadataPath);
                if (storedMetadata is not null)
                {
                    metadata = MergeDailyMetadata(storedMetadata, metadata);
                }

                metadata.MessageCount = Math.Max(metadata.MessageCount, CountLines(Path.Combine(existingDirectory, "chat.jsonl")));
                _messageCounts[metadataPath] = metadata.MessageCount;
                await WriteMetadataAsync(metadataPath, metadata, cancellationToken).ConfigureAwait(false);
            }

            var state = new SessionState(
                root,
                channelDirectory,
                existingDirectory ?? string.Empty,
                metadataPath,
                streamStatus.IsLive,
                streamStatus.StartedAt?.ToUniversalTime(),
                metadata,
                settings.SaveChatLogTxt,
                settings.LogChatBadges,
                settings.Language);
            lock (_sessionSync)
            {
                _session = state;
                _sessionCreationTask = null;
            }

            if (!reuseCurrentSession)
            {
                CleanupEmptySession(current);
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
        if (!streamStatus.IsAuthoritative)
        {
            return;
        }

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

    public async Task UpdateChannelStreamInfoAsync(
        string channelLogin,
        StreamStatusInfo streamStatus,
        CancellationToken cancellationToken = default)
    {
        if (!streamStatus.IsAuthoritative)
        {
            return;
        }

        var login = (channelLogin ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();
        var currentDailyKey = CreateChannelSessionKey(login, DateTimeOffset.Now);
        var entries = _channelSessions
            .Where(entry => entry.Key.Equals(currentDailyKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var entry in entries)
        {
            try
            {
                var session = await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (session is null)
                {
                    continue;
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

                _channelSessions.TryUpdate(
                    entry.Key,
                    Task.FromResult<SessionState?>(updated),
                    entry.Value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Channel chat log metadata update failed: {ex.GetType().Name}");
            }
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

            if (!IsSameLocalLogDate(session.Metadata, message.Timestamp))
            {
                session = CreateNextDailySession(session, message.Timestamp);
                _session = session;
                _sessionCreationTask = null;
            }

            if (_sessionCreationTask is { IsCompleted: true } completedCreation &&
                (!completedCreation.IsCompletedSuccessfully || completedCreation.Result is null))
            {
                _sessionCreationTask = null;
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
                JsonSerializer.Serialize(entry, JsonOptions),
                session.SaveTxt ? FormatTextLine(entry, session.Language) : null);
            if (!_queue.Writer.TryWrite(queued))
            {
                ReportQueueFull();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Chat log enqueue failed: {ex.GetType().Name}");
            NotifyWriteFailed();
        }
    }

    public void Enqueue(
        AppSettings settings,
        string channelLogin,
        string broadcasterId,
        string displayName,
        StreamStatusInfo streamStatus,
        ChatMessageModel message)
    {
        if (!IsRealChatMessage(message))
        {
            return;
        }

        settings.Normalize();
        if (!settings.EnableChatLogging)
        {
            return;
        }

        var login = (channelLogin ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(login))
        {
            return;
        }

        try
        {
            var broadcaster = new TwitchUser
            {
                Id = broadcasterId ?? string.Empty,
                Login = login,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? login : displayName
            };
            var state = CreateArmedChannelSession(settings, broadcaster, streamStatus, message.Timestamp);
            var sessionKey = CreateChannelSessionKey(login, message.Timestamp);
            var sessionTask = GetOrCreateChannelSessionTask(sessionKey, state);
            var entry = CreateEntry(state, message);
            var queued = new QueuedLogMessage(
                sessionTask,
                string.Empty,
                string.Empty,
                JsonSerializer.Serialize(entry, JsonOptions),
                state.SaveTxt ? FormatTextLine(entry, state.Language) : null);
            if (!_queue.Writer.TryWrite(queued))
            {
                ReportQueueFull();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Channel chat log enqueue failed: {ex.GetType().Name}");
            NotifyWriteFailed();
        }
    }

    public async Task StopChannelSessionAsync(string channelLogin)
    {
        var login = (channelLogin ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();
        var entries = _channelSessions
            .Where(entry => IsChannelSessionKey(entry.Key, login))
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        await FlushAsync().ConfigureAwait(false);
        foreach (var entry in entries)
        {
            if (_channelSessions.TryRemove(entry.Key, out var sessionTask))
            {
                CleanupEmptySession(await AwaitSessionTaskSafelyAsync(sessionTask).ConfigureAwait(false));
            }
        }
    }

    public async Task ResetChannelSessionsAsync()
    {
        var sessions = _channelSessions.Values.ToArray();
        _channelSessions.Clear();
        await FlushAsync().ConfigureAwait(false);
        foreach (var session in sessions)
        {
            CleanupEmptySession(await AwaitSessionTaskSafelyAsync(session).ConfigureAwait(false));
        }
    }

    private async Task<SessionState?> AwaitSessionTaskSafelyAsync(Task<SessionState?> task)
    {
        try
        {
            return await task.WaitAsync(FlushTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Chat log session operation did not finish: {ex.GetType().Name}");
            return null;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        timeout.CancelAfter(FlushTimeout);
        try
        {
            await _queue.Writer.WriteAsync(QueuedLogMessage.Flush(completion), timeout.Token).ConfigureAwait(false);
            await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.Warn("Chat log flush timed out or was canceled.");
            NotifyWriteFailed();
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
        Task<SessionState?>? primarySessionTask;
        var channelSessions = _channelSessions.Values.ToArray();
        _channelSessions.Clear();
        lock (_sessionSync)
        {
            session = _session;
            _session = null;
            primarySessionTask = _sessionCreationTask;
            _sessionCreationTask = null;
        }

        _queue.Writer.TryComplete();
        var writerStopped = false;
        try
        {
            if (await Task.WhenAny(_writerTask, Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false) != _writerTask)
            {
                _lifetimeCts.Cancel();
                if (await Task.WhenAny(_writerTask, Task.Delay(ShutdownCancelTimeout)).ConfigureAwait(false) != _writerTask)
                {
                    _logger.Warn("Chat log writer did not stop before the shutdown deadline.");
                    _ = _writerTask.ContinueWith(
                        completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }
            }

            await _writerTask.ConfigureAwait(false);
            writerStopped = true;
            CleanupEmptySession(session);
            if (primarySessionTask?.IsCompletedSuccessfully == true)
            {
                CleanupEmptySession(primarySessionTask.Result);
            }
            foreach (var channelSession in channelSessions.Where(task => task.IsCompletedSuccessfully))
            {
                CleanupEmptySession(channelSession.Result);
            }
        }
        catch
        {
            // Chat logging must never block app shutdown.
        }
        finally
        {
            if (writerStopped || _writerTask.IsCompleted)
            {
                _sessionLock.Dispose();
                _lifetimeCts.Dispose();
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        var batch = new List<QueuedLogMessage>(64);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_lifetimeCts.Token).ConfigureAwait(false))
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
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var pending))
            {
                pending.FlushCompletion?.TrySetCanceled(_lifetimeCts.Token);
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
            var jsonLines = group.Select(item => item.JsonLine).Where(line => line is not null).Cast<string>().ToList();
            var textLines = group.Select(item => item.TextLine).Where(line => line is not null).Cast<string>().ToList();
            var jsonWritten = jsonLines.Count > 0 &&
                              await TryAppendLinesAsync(Path.Combine(group.Key, "chat.jsonl"), jsonLines, _lifetimeCts.Token).ConfigureAwait(false);
            var textWritten = textLines.Count > 0 &&
                              await TryAppendLinesAsync(Path.Combine(group.Key, "chat.txt"), textLines, _lifetimeCts.Token).ConfigureAwait(false);
            var written = Math.Max(jsonWritten ? jsonLines.Count : 0, textWritten ? textLines.Count : 0);
            if (written <= 0)
            {
                continue;
            }

            var metadataPath = group.First().MetadataPath;
            var count = _messageCounts.AddOrUpdate(metadataPath, written, (_, current) => current + written);
            try
            {
                await UpdateMessageCountAsync(metadataPath, count, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Chat log metadata write failed: {ex.GetType().Name}");
                NotifyWriteFailed();
            }
        }
    }

    private async Task<bool> TryAppendLinesAsync(
        string path,
        IReadOnlyCollection<string> lines,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(path)!;
                EnsureSafeLogFilePath(path);
                Directory.CreateDirectory(directory);
                EnsureSafeLogFilePath(path);
                await File.AppendAllLinesAsync(path, lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < MaxWriteAttempts)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1))),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                break;
            }
        }

        _logger.Warn($"Chat log write failed after {MaxWriteAttempts} attempts: {lastError?.GetType().Name ?? "Unknown"}");
        NotifyWriteFailed();
        return false;
    }

    private void NotifyWriteFailed()
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref _lastWriteWarningTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(30))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastWriteWarningTimestamp, now, previous) == previous)
        {
            WriteFailed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReportQueueFull()
    {
        Interlocked.Increment(ref _droppedQueueMessages);
        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref _lastQueueFullWarningTimestamp);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMinutes(1))
        {
            NotifyWriteFailed();
            return;
        }

        if (Interlocked.CompareExchange(ref _lastQueueFullWarningTimestamp, now, previous) == previous)
        {
            var dropped = Interlocked.Exchange(ref _droppedQueueMessages, 0);
            _logger.Warn($"Chat log queue is full; {Math.Max(1, dropped)} messages were dropped.");
        }
        NotifyWriteFailed();
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
            if (!IsChildPath(session.RootDirectory, session.ChannelDirectory) ||
                ContainsReparsePoint(session.RootDirectory, session.ChannelDirectory))
            {
                throw new IOException("Unsafe chat log channel path.");
            }
            Directory.CreateDirectory(session.ChannelDirectory);
            if (ContainsReparsePoint(session.RootDirectory, session.ChannelDirectory))
            {
                throw new IOException("Unsafe chat log channel path.");
            }
            var existingDirectory = FindDailySessionDirectory(
                session.ChannelDirectory,
                session.Metadata.LogStartedAtLocal);
            var sessionDirectory = existingDirectory ?? CreateSessionDirectory(session.ChannelDirectory, session.Metadata);
            if (!IsChildPath(session.ChannelDirectory, sessionDirectory) ||
                ContainsReparsePoint(session.RootDirectory, sessionDirectory))
            {
                throw new IOException("Unsafe chat log session path.");
            }
            Directory.CreateDirectory(sessionDirectory);
            if (ContainsReparsePoint(session.RootDirectory, sessionDirectory))
            {
                throw new IOException("Unsafe chat log session path.");
            }

            var metadataPath = Path.Combine(sessionDirectory, "metadata.json");
            var storedMetadata = ReadMetadata(metadataPath);
            var metadata = storedMetadata is null
                ? session.Metadata
                : MergeDailyMetadata(storedMetadata, session.Metadata);
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
            lock (_sessionSync)
            {
                if (ReferenceEquals(_session, session))
                {
                    _sessionCreationTask = null;
                }
            }
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

    private Task<SessionState?> CreateIndependentSessionInBackgroundAsync(SessionState session) =>
        Task.Run(() => EnsureSessionCreated(session));

    private Task<SessionState?> GetOrCreateChannelSessionTask(string sessionKey, SessionState state)
    {
        while (true)
        {
            if (_channelSessions.TryGetValue(sessionKey, out var existing))
            {
                if (!existing.IsCompleted || existing.IsCompletedSuccessfully && existing.Result is not null)
                {
                    return existing;
                }

                TryRemoveChannelSessionTask(sessionKey, existing);
                continue;
            }

            var created = CreateIndependentSessionInBackgroundAsync(state);
            if (_channelSessions.TryAdd(sessionKey, created))
            {
                _ = created.ContinueWith(
                    completed =>
                    {
                        if (!completed.IsCompletedSuccessfully || completed.Result is null)
                        {
                            TryRemoveChannelSessionTask(sessionKey, completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return created;
            }
        }
    }

    private bool TryRemoveChannelSessionTask(string sessionKey, Task<SessionState?> task) =>
        ((ICollection<KeyValuePair<string, Task<SessionState?>>>)_channelSessions)
        .Remove(new KeyValuePair<string, Task<SessionState?>>(sessionKey, task));

    private static SessionState CreateArmedChannelSession(
        AppSettings settings,
        TwitchUser broadcaster,
        StreamStatusInfo streamStatus,
        DateTimeOffset timestamp)
    {
        var root = GetRootFolder(settings);
        var login = broadcaster.Login.Trim().TrimStart('@', '#').ToLowerInvariant();
        var channelDirectory = Path.Combine(root, SanitizePathSegment(login));
        return new SessionState(
            root,
            channelDirectory,
            string.Empty,
            string.Empty,
            streamStatus.IsLive,
            streamStatus.StartedAt?.ToUniversalTime(),
            CreateMetadata(broadcaster, streamStatus, timestamp),
            settings.SaveChatLogTxt,
            settings.LogChatBadges,
            settings.Language);
    }

    private static string CreateChannelSessionKey(string login, DateTimeOffset timestamp) =>
        $"{login}|daily|{timestamp.ToLocalTime():yyyy-MM-dd}";

    private static bool IsChannelSessionKey(string key, string login) =>
        key.Equals(login, StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(login + "|", StringComparison.OrdinalIgnoreCase);

    private static bool IsRealChatMessage(ChatMessageModel message)
    {
        if (message is null)
        {
            return false;
        }

        if (message.IsChannelPointsRedemption)
        {
            return !string.IsNullOrWhiteSpace(message.RedemptionId);
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

    private static bool ShouldReuseSession(SessionState? current, string channelLogin, StreamStatusInfo _)
    {
        if (current is null ||
            !string.Equals(current.Metadata.ChannelLogin, channelLogin, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return current.Metadata.LogStartedAtLocal.LocalDateTime.Date == DateTime.Now.Date;
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
                if (!IsChildPath(root, channelDirectory) || ContainsReparsePoint(root, channelDirectory))
                {
                    continue;
                }

                foreach (var sessionDirectory in Directory.EnumerateDirectories(channelDirectory))
                {
                    if (IsOwnedLogSessionDirectory(root, sessionDirectory) &&
                        IsEmptyLogSessionDirectory(sessionDirectory))
                    {
                        DeleteOwnedLogSessionDirectory(sessionDirectory);
                    }
                }

                DeleteEmptyOwnedChannelDirectory(root, channelDirectory);
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
            !IsOwnedLogSessionDirectory(session.RootDirectory, session.DirectoryPath))
        {
            return;
        }

        try
        {
            if (IsEmptyLogSessionDirectory(session.DirectoryPath))
            {
                DeleteOwnedLogSessionDirectory(session.DirectoryPath);
            }

            DeleteEmptyOwnedChannelDirectory(session.RootDirectory, session.ChannelDirectory);
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

    private static bool IsOwnedLogSessionDirectory(string root, string sessionDirectory)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            var sessionFull = Path.GetFullPath(sessionDirectory);
            if (!Directory.Exists(sessionFull) ||
                !IsChildPath(rootFull, sessionFull) ||
                ContainsReparsePoint(rootFull, sessionFull))
            {
                return false;
            }

            var channelDirectory = Directory.GetParent(sessionFull)?.FullName;
            if (string.IsNullOrWhiteSpace(channelDirectory) ||
                !IsChildPath(rootFull, channelDirectory) ||
                ContainsReparsePoint(rootFull, channelDirectory))
            {
                return false;
            }

            var metadata = ReadMetadata(Path.Combine(sessionFull, "metadata.json"));
            var expectedChannelDirectoryName = string.IsNullOrWhiteSpace(metadata?.ChannelLogin)
                ? string.Empty
                : SanitizePathSegment(metadata.ChannelLogin.Trim().TrimStart('@', '#').ToLowerInvariant());
            if (string.IsNullOrWhiteSpace(expectedChannelDirectoryName) ||
                !string.Equals(Path.GetFileName(channelDirectory), expectedChannelDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(sessionFull))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint) || Directory.Exists(entry))
                {
                    return false;
                }

                var fileName = Path.GetFileName(entry);
                if (!fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.Equals("chat.jsonl", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.Equals("chat.txt", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteOwnedLogSessionDirectory(string sessionDirectory)
    {
        foreach (var fileName in new[] { "chat.jsonl", "chat.txt", "metadata.json" })
        {
            var path = Path.Combine(sessionDirectory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Directory.Delete(sessionDirectory, recursive: false);
    }

    private static void DeleteEmptyOwnedChannelDirectory(string root, string channelDirectory)
    {
        if (Directory.Exists(channelDirectory) &&
            IsChildPath(root, channelDirectory) &&
            !ContainsReparsePoint(root, channelDirectory) &&
            !Directory.EnumerateFileSystemEntries(channelDirectory).Any())
        {
            Directory.Delete(channelDirectory, recursive: false);
        }
    }

    private static bool HasMessageLines(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var reader = OpenSharedTextReader(path);
            while (reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return true;
                }
            }

            return false;
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
                Title = badge.Title,
                ImageUrl = badge.ImageUrl
            }).ToList()
            : [];

        return new ChatLogMessageEntry
        {
            TimestampLocal = timestampLocal,
            TimestampUtc = timestampUtc,
            ChannelLogin = session.Metadata.ChannelLogin,
            BroadcasterId = session.Metadata.BroadcasterId,
            RoomId = message.RoomId,
            SourceRoomId = message.SourceRoomId,
            StreamTitle = session.Metadata.StreamTitle,
            UserId = message.UserId,
            UserLogin = message.Login,
            DisplayName = message.DisplayName,
            UserColor = message.Color,
            Badges = badges,
            Message = message.Text,
            MessageId = message.MessageId,
            RelatedMessageId = message.RelatedMessageId,
            ReplyParentMessageId = message.ReplyParentMessageId,
            ReplyParentUserId = message.ReplyParentUserId,
            ReplyParentUserLogin = message.ReplyParentUserLogin,
            ReplyParentDisplayName = message.ReplyParentDisplayName,
            ReplyParentMessageBody = message.ReplyParentMessageBody,
            ModerationState = message.ModerationState,
            ModeratedAt = message.ModeratedAt,
            ModeratorId = message.ModeratedByUserId,
            ModeratorName = message.ModeratedByDisplayName,
            ModerationReason = message.ModerationReason,
            Kind = message.Kind,
            RedemptionId = message.RedemptionId,
            RewardId = message.RewardId,
            RewardTitle = message.RewardTitle,
            RewardCost = message.RewardCost,
            RewardPrompt = message.RewardPrompt,
            RewardUserInput = message.RewardUserInput,
            RewardType = message.RewardType,
            RedeemedAt = message.RedeemedAt,
            IsModerator = message.IsModerator,
            IsSubscriber = message.IsSubscriber,
            IsBroadcaster = message.Badges.Any(b => string.Equals(b.SetId, "broadcaster", StringComparison.OrdinalIgnoreCase)),
            IsVip = message.IsVip
        };
    }

    private static bool Matches(ChatLogMessageEntry entry, string searchText, string userFilter, string roleFilter)
    {
        var userLabel = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.UserLogin : entry.DisplayName;
        if (!string.IsNullOrWhiteSpace(searchText) &&
            !ContainsIgnoreCase(entry.Message, searchText) &&
            !ContainsIgnoreCase(userLabel, searchText) &&
            !ContainsIgnoreCase(entry.UserLogin, searchText) &&
            !ContainsIgnoreCase(entry.RewardTitle, searchText) &&
            !ContainsIgnoreCase(entry.RewardUserInput, searchText) &&
            !ContainsIgnoreCase(entry.RewardType, searchText))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(userFilter) &&
            !ContainsIgnoreCase(entry.UserLogin, userFilter) &&
            !ContainsIgnoreCase(userLabel, userFilter))
        {
            return false;
        }

        return entry.MatchesRole(roleFilter);
    }

    private static bool NormalizeLogEntry(ChatLogMessageEntry entry)
    {
        if (entry.TimestampLocal == default && entry.TimestampUtc == default)
        {
            return false;
        }

        entry.ChannelLogin ??= string.Empty;
        entry.BroadcasterId ??= string.Empty;
        entry.RoomId ??= string.Empty;
        entry.SourceRoomId ??= string.Empty;
        entry.StreamTitle ??= string.Empty;
        entry.UserId ??= string.Empty;
        entry.UserLogin ??= string.Empty;
        entry.DisplayName ??= string.Empty;
        entry.UserColor ??= string.Empty;
        entry.Message ??= string.Empty;
        entry.MessageId ??= string.Empty;
        entry.RelatedMessageId ??= string.Empty;
        entry.ReplyParentMessageId ??= string.Empty;
        entry.ReplyParentUserId ??= string.Empty;
        entry.ReplyParentUserLogin ??= string.Empty;
        entry.ReplyParentDisplayName ??= string.Empty;
        entry.ReplyParentMessageBody ??= string.Empty;
        entry.ModeratorId ??= string.Empty;
        entry.ModeratorName ??= string.Empty;
        entry.ModerationReason ??= string.Empty;
        entry.RedemptionId ??= string.Empty;
        entry.RewardId ??= string.Empty;
        entry.RewardTitle ??= string.Empty;
        entry.RewardPrompt ??= string.Empty;
        entry.RewardUserInput ??= string.Empty;
        entry.RewardType ??= string.Empty;
        entry.Badges = (entry.Badges ?? [])
            .Where(static badge => badge is not null)
            .ToList();
        foreach (var badge in entry.Badges)
        {
            badge.SetId ??= string.Empty;
            badge.Id ??= string.Empty;
            badge.Info ??= string.Empty;
            badge.Title ??= string.Empty;
            badge.ImageUrl = string.Empty;
        }

        return true;
    }

    private static bool ContainsIgnoreCase(string? source, string value) =>
        !string.IsNullOrEmpty(source) && source.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string FormatTextLine(ChatLogMessageEntry entry, string language)
    {
        if (!entry.IsChannelPointsRedemption)
        {
            return $"[{entry.TimestampLocal.LocalDateTime:HH:mm:ss}] {entry.UserLabel}: {entry.Message}";
        }

        var line = entry.RewardCost is { } rewardCost
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Get(language, "ChatLogRedeemedForPointsFormat"),
                entry.TimestampLocal.LocalDateTime,
                entry.UserLabel,
                entry.RewardTitle,
                rewardCost)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Get(language, "ChatLogRedeemedFormat"),
                entry.TimestampLocal.LocalDateTime,
                entry.UserLabel,
                entry.RewardTitle);
        return string.IsNullOrWhiteSpace(entry.RewardUserInput)
            ? line
            : line + Environment.NewLine + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.Get(language, "ChatLogUserInputFormat"),
                entry.RewardUserInput);
    }

    private static ChatLogSessionSummary? TryCreateSessionSummary(string directoryPath, string expectedChannelLogin)
    {
        var metadataPath = Path.Combine(directoryPath, "metadata.json");
        var metadata = ReadMetadata(metadataPath);
        if (metadata is null ||
            string.IsNullOrWhiteSpace(metadata.ChannelLogin) ||
            metadata.LogStartedAtLocal == default ||
            metadata.LogStartedAtUtc == default ||
            !string.Equals(
                SanitizePathSegment(metadata.ChannelLogin.Trim().TrimStart('@', '#').ToLowerInvariant()),
                expectedChannelLogin,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

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

    private static string? FindDailySessionDirectory(string channelDirectory, DateTimeOffset logDateLocal)
    {
        if (!Directory.Exists(channelDirectory))
        {
            return null;
        }

        var directory = Path.Combine(channelDirectory, $"{logDateLocal.LocalDateTime:yyyy-MM-dd}_chat");
        return Directory.Exists(directory) ? directory : null;
    }

    private static string CreateSessionDirectory(string channelDirectory, ChatLogSessionMetadata metadata)
    {
        return Path.Combine(
            channelDirectory,
            $"{metadata.LogStartedAtLocal.LocalDateTime:yyyy-MM-dd}_chat");
    }

    private static ChatLogSessionMetadata CreateMetadata(
        TwitchUser broadcaster,
        StreamStatusInfo status,
        DateTimeOffset? timestamp = null)
    {
        var now = (timestamp ?? DateTimeOffset.Now).ToLocalTime();
        var channelLogin = string.IsNullOrWhiteSpace(broadcaster.Login)
            ? "unknown-channel"
            : broadcaster.Login.Trim().TrimStart('@').ToLowerInvariant();

        return new ChatLogSessionMetadata
        {
            LogMode = "daily",
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

        return string.Empty;
    }

    private static bool IsSameLocalLogDate(ChatLogSessionMetadata metadata, DateTimeOffset timestamp) =>
        metadata.LogStartedAtLocal.LocalDateTime.Date == timestamp.ToLocalTime().Date;

    private static SessionState CreateNextDailySession(SessionState current, DateTimeOffset timestamp)
    {
        var localTimestamp = timestamp.ToLocalTime();
        var metadata = new ChatLogSessionMetadata
        {
            LogMode = "daily",
            ChannelLogin = current.Metadata.ChannelLogin,
            ChannelDisplayName = current.Metadata.ChannelDisplayName,
            BroadcasterId = current.Metadata.BroadcasterId,
            StreamTitle = current.IsLive ? current.Metadata.StreamTitle : string.Empty,
            GameName = current.IsLive ? current.Metadata.GameName : string.Empty,
            StreamStartedAtUtc = current.IsLive ? current.StreamStartedAtUtc : null,
            LogStartedAtLocal = localTimestamp,
            LogStartedAtUtc = localTimestamp.ToUniversalTime(),
            IsLive = current.IsLive,
            AppVersion = AppInfo.Version
        };
        return current with
        {
            DirectoryPath = string.Empty,
            MetadataPath = string.Empty,
            Metadata = metadata
        };
    }

    private static ChatLogSessionMetadata MergeDailyMetadata(
        ChatLogSessionMetadata stored,
        ChatLogSessionMetadata incoming)
    {
        stored.LogMode = "daily";
        stored.ChannelLogin = string.IsNullOrWhiteSpace(incoming.ChannelLogin)
            ? stored.ChannelLogin
            : incoming.ChannelLogin;
        stored.ChannelDisplayName = string.IsNullOrWhiteSpace(incoming.ChannelDisplayName)
            ? stored.ChannelDisplayName
            : incoming.ChannelDisplayName;
        stored.BroadcasterId = string.IsNullOrWhiteSpace(incoming.BroadcasterId)
            ? stored.BroadcasterId
            : incoming.BroadcasterId;
        stored.IsLive = incoming.IsLive;
        if (incoming.IsLive && !string.IsNullOrWhiteSpace(incoming.StreamTitle))
        {
            stored.StreamTitle = incoming.StreamTitle;
            stored.GameName = incoming.GameName;
            stored.StreamStartedAtUtc = incoming.StreamStartedAtUtc;
        }

        if (stored.LogStartedAtLocal == default)
        {
            stored.LogStartedAtLocal = incoming.LogStartedAtLocal;
        }

        if (stored.LogStartedAtUtc == default)
        {
            stored.LogStartedAtUtc = incoming.LogStartedAtUtc;
        }

        stored.AppVersion = AppInfo.Version;
        return stored;
    }

    private static async Task UpdateMessageCountAsync(
        string metadataPath,
        long count,
        CancellationToken cancellationToken)
    {
        using (await AcquireMetadataWriteLockAsync(metadataPath, cancellationToken).ConfigureAwait(false))
        {
            var metadata = ReadMetadata(metadataPath);
            if (metadata is null)
            {
                return;
            }

            metadata.MessageCount = Math.Max(metadata.MessageCount, count);
            await WriteMetadataFileAtomicAsync(metadataPath, metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ChatLogSessionMetadata? ReadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = OpenSharedTextReader(path);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<ChatLogSessionMetadata>(json, MetadataJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteMetadataAsync(string path, ChatLogSessionMetadata metadata, CancellationToken cancellationToken)
    {
        using (await AcquireMetadataWriteLockAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var stored = ReadMetadata(path);
            metadata.MessageCount = Math.Max(metadata.MessageCount, stored?.MessageCount ?? 0);
            await WriteMetadataFileAtomicAsync(path, metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteMetadata(string path, ChatLogSessionMetadata metadata)
    {
        WriteMetadataAsync(path, metadata, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async ValueTask<MetadataWriteLockLease> AcquireMetadataWriteLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(path);
        MetadataWriteLockEntry entry;
        lock (MetadataWriteLocksGate)
        {
            if (!MetadataWriteLocks.TryGetValue(key, out entry!))
            {
                entry = new MetadataWriteLockEntry();
                MetadataWriteLocks.Add(key, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new MetadataWriteLockLease(key, entry);
        }
        catch
        {
            ReleaseMetadataWriteLockReference(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private static void ReleaseMetadataWriteLockReference(
        string key,
        MetadataWriteLockEntry entry,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        var dispose = false;
        lock (MetadataWriteLocksGate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                MetadataWriteLocks.TryGetValue(key, out var current) &&
                ReferenceEquals(current, entry))
            {
                MetadataWriteLocks.Remove(key);
                dispose = true;
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private static async Task WriteMetadataFileAtomicAsync(
        string path,
        ChatLogSessionMetadata metadata,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureSafeLogFilePath(path);
        Directory.CreateDirectory(directory);
        EnsureSafeLogFilePath(path);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A stale temporary metadata file is harmless and can be cleaned up later.
            }
        }
    }

    private static void EnsureSafeLogFilePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var sessionDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new IOException("Log path has no session directory.");
        var channelDirectory = Directory.GetParent(sessionDirectory)?.FullName
            ?? throw new IOException("Log path has no channel directory.");
        var rootDirectory = Directory.GetParent(channelDirectory)?.FullName
            ?? throw new IOException("Log path has no root directory.");
        var fileName = Path.GetFileName(fullPath);
        if ((!fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase) &&
             !fileName.Equals("chat.jsonl", StringComparison.OrdinalIgnoreCase) &&
             !fileName.Equals("chat.txt", StringComparison.OrdinalIgnoreCase)) ||
            !IsChildPath(rootDirectory, channelDirectory) ||
            !IsChildPath(channelDirectory, sessionDirectory) ||
            ContainsReparsePoint(rootDirectory, sessionDirectory))
        {
            throw new IOException("Unsafe chat log path.");
        }
    }

    private static long CountLines(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            long count = 0;
            using var reader = OpenSharedTextReader(path, 64 * 1024);
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return count;
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

        var deviceName = result.Split('.', 2)[0];
        if (ReservedWindowsDeviceNames.Contains(deviceName))
        {
            result = "_" + result;
        }

        return result.Length > 90 ? result[..90].TrimEnd(' ', '.', '-') : result;
    }

    private static StreamReader OpenSharedTextReader(string path, int bufferSize = 16 * 1024)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.SequentialScan);
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    }

    private static IEnumerable<string> ReadLinesBackwards(
        string path,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 64 * 1024;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize,
            FileOptions.RandomAccess);
        var buffer = new byte[bufferSize];
        var pending = Array.Empty<byte>();
        var discardOversizedLine = false;
        var position = stream.Length;

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, position);
            position -= count;
            stream.Position = position;
            var read = 0;
            while (read < count)
            {
                var current = stream.Read(buffer, read, count - read);
                if (current == 0)
                {
                    throw new EndOfStreamException();
                }

                read += current;
            }

            var lineEnd = count;
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                if (!discardOversizedLine)
                {
                    var segmentLength = lineEnd - index - 1;
                    var line = DecodeLogLine(buffer, index + 1, segmentLength, pending);
                    if (line is not null)
                    {
                        yield return line;
                    }
                }

                discardOversizedLine = false;
                pending = Array.Empty<byte>();
                lineEnd = index;
            }

            if (lineEnd == 0)
            {
                continue;
            }

            if (lineEnd + pending.Length > MaxLogLineBytes)
            {
                pending = Array.Empty<byte>();
                discardOversizedLine = true;
                continue;
            }

            var combined = new byte[lineEnd + pending.Length];
            Buffer.BlockCopy(buffer, 0, combined, 0, lineEnd);
            Buffer.BlockCopy(pending, 0, combined, lineEnd, pending.Length);
            pending = combined;
        }

        if (!discardOversizedLine && pending.Length > 0)
        {
            var line = DecodeLogLine(pending, 0, pending.Length, []);
            if (line is not null)
            {
                yield return line;
            }
        }
    }

    private static string? DecodeLogLine(
        byte[] segment,
        int offset,
        int count,
        byte[] suffix)
    {
        var total = count + suffix.Length;
        if (total == 0 || total > MaxLogLineBytes)
        {
            return null;
        }

        var bytes = new byte[total];
        Buffer.BlockCopy(segment, offset, bytes, 0, count);
        Buffer.BlockCopy(suffix, 0, bytes, count, suffix.Length);
        if (total > 0 && bytes[total - 1] == (byte)'\r')
        {
            total--;
        }

        var start = total >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return total <= start ? null : Encoding.UTF8.GetString(bytes, start, total - start);
    }

    private static bool HasSameDirectory(SessionState? session, string directoryPath) =>
        session is not null &&
        !string.IsNullOrWhiteSpace(session.DirectoryPath) &&
        string.Equals(
            Path.GetFullPath(session.DirectoryPath),
            directoryPath,
            StringComparison.OrdinalIgnoreCase);

    private sealed class MetadataWriteLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class MetadataWriteLockLease(
        string key,
        MetadataWriteLockEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseMetadataWriteLockReference(key, entry, releaseSemaphore: true);
            }
        }
    }

    private sealed record SessionState(
        string RootDirectory,
        string ChannelDirectory,
        string DirectoryPath,
        string MetadataPath,
        bool IsLive,
        DateTimeOffset? StreamStartedAtUtc,
        ChatLogSessionMetadata Metadata,
        bool SaveTxt,
        bool LogBadges,
        string Language);

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
