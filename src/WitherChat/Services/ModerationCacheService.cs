using System.IO;
using System.Text;
using System.Text.Json;
using WitherChat.Models;
using WitherChat.ViewModels;

namespace WitherChat.Services;

public sealed class ModerationCacheService : IAsyncDisposable
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const int MaximumChannels = 32;
    private const int MaximumBannedUsersPerChannel = 1000;
    private const int MaximumRequestsPerChannel = 500;
    private static readonly TimeSpan CacheRetention = TimeSpan.FromDays(30);
    private static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("WitherChat.ModerationCache.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly FileLogger _logger;
    private ModerationCacheDocument _document;
    private CancellationTokenSource? _debounceCts;
    private Task _pendingSaveTask = Task.CompletedTask;

    public ModerationCacheService(FileLogger logger)
    {
        _logger = logger;
        _document = LoadDocument();
    }

    public bool RestoreSession(ChannelSessionViewModel session)
    {
        if (string.IsNullOrWhiteSpace(session.BroadcasterId))
        {
            return false;
        }

        ModerationChannelCache? cached;
        lock (_gate)
        {
            _document.Channels.TryGetValue(session.BroadcasterId, out cached);
        }
        if (cached is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var item in cached.BannedUsers.Where(item => item.ExpiresAt is null || item.ExpiresAt > now))
        {
            if (session.BannedUsers.Any(existing => string.Equals(existing.UserId, item.UserId, StringComparison.Ordinal)))
            {
                continue;
            }
            session.BannedUsers.Add(new BannedUserEntry
            {
                UserId = item.UserId,
                UserLogin = item.UserLogin,
                DisplayName = item.DisplayName,
                CreatedAt = item.CreatedAt,
                ExpiresAt = item.ExpiresAt,
                Reason = item.Reason
            });
            session.ActivePunishments[item.UserId] = new ActivePunishmentState
            {
                UserId = item.UserId,
                UserLogin = item.UserLogin,
                DisplayName = item.DisplayName,
                Type = item.ExpiresAt is null ? PunishmentType.Ban : PunishmentType.Timeout,
                StartedAt = item.CreatedAt,
                EndsAt = item.ExpiresAt,
                ModeratorId = item.ModeratorId,
                ModeratorName = item.ModeratorName,
                Reason = item.Reason,
                Source = item.Source,
                LastUpdatedAt = item.LastConfirmedAt
            };
        }

        foreach (var item in cached.UnbanRequests.Where(item => item.Status == UnbanRequestStatus.Pending))
        {
            if (session.UnbanRequests.Any(existing => string.Equals(existing.RequestId, item.RequestId, StringComparison.Ordinal)))
            {
                continue;
            }
            session.UnbanRequests.Add(item.ToModel());
        }

        session.HasCachedBannedUsers = session.BannedUsers.Count > 0;
        session.IsBannedUsersDataStale = session.HasCachedBannedUsers;
        return session.HasCachedBannedUsers || session.UnbanRequests.Count > 0;
    }

    public void ScheduleSave(ChannelSessionViewModel session)
    {
        if (string.IsNullOrWhiteSpace(session.BroadcasterId))
        {
            return;
        }

        lock (_gate)
        {
            _document.Channels[session.BroadcasterId] = ModerationChannelCache.FromSession(session);
            while (_document.Channels.Count > MaximumChannels)
            {
                var oldest = _document.Channels.MinBy(pair => pair.Value.LastUpdatedAt).Key;
                _document.Channels.Remove(oldest);
            }
            _debounceCts?.Cancel();
            var next = new CancellationTokenSource();
            _debounceCts = next;
            _pendingSaveTask = SaveAfterDelayAsync(next);
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? pending;
        Task pendingTask;
        lock (_gate)
        {
            pending = _debounceCts;
            _debounceCts = null;
            pendingTask = _pendingSaveTask;
            _pendingSaveTask = Task.CompletedTask;
            _document = new ModerationCacheDocument();
        }

        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await pendingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Moderation cache pending save cleanup failed: {ex.GetType().Name}");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cacheFileName = Path.GetFileName(AppPaths.ModerationCacheFile);
            var paths = new[]
                {
                    AppPaths.ModerationCacheFile,
                    AppPaths.ModerationCacheFile + ".tmp"
                }
                .Concat(Directory.Exists(AppPaths.LocalDataDirectory)
                    ? Directory.EnumerateFiles(
                        AppPaths.LocalDataDirectory,
                        cacheFileName + ".corrupt-*",
                        SearchOption.TopDirectoryOnly)
                    : [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var path in paths)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Moderation cache file cleanup failed: {ex.GetType().Name}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"Moderation cache save failed: {ex.GetType().Name}");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_debounceCts, cancellation))
                {
                    _debounceCts = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ModerationCacheDocument snapshot;
            lock (_gate)
            {
                snapshot = _document.Clone();
            }

            Directory.CreateDirectory(AppPaths.LocalDataDirectory);
            var tempPath = AppPaths.ModerationCacheFile + ".tmp";
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            var encrypted = LocalDataProtection.Protect(json, CacheEntropy, "WitherChat moderation cache");
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, AppPaths.ModerationCacheFile, true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ModerationCacheDocument LoadDocument()
    {
        try
        {
            if (!File.Exists(AppPaths.ModerationCacheFile))
            {
                return new ModerationCacheDocument();
            }
            var info = new FileInfo(AppPaths.ModerationCacheFile);
            if (info.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Moderation cache exceeds the safety limit.");
            }
            var stored = File.ReadAllBytes(info.FullName);
            var json = LooksLikePlaintextJson(stored)
                ? stored
                : LocalDataProtection.Unprotect(stored, CacheEntropy);
            var document = JsonSerializer.Deserialize<ModerationCacheDocument>(json, JsonOptions);
            return NormalizeAndValidate(document);
        }
        catch (Exception ex)
        {
            PreserveCorruptDocument();
            _logger.Warn($"Moderation cache ignored: {ex.GetType().Name}");
            return new ModerationCacheDocument();
        }
    }

    private static bool LooksLikePlaintextJson(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        while (index < bytes.Length && bytes[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        return index < bytes.Length && bytes[index] == (byte)'{';
    }

    private static ModerationCacheDocument NormalizeAndValidate(ModerationCacheDocument? document)
    {
        if (document?.Channels is null)
        {
            throw new InvalidDataException("Moderation cache does not contain a valid channel map.");
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - CacheRetention;
        var channels = new Dictionary<string, ModerationChannelCache>(StringComparer.Ordinal);
        foreach (var (channelId, channel) in document.Channels
                     .OrderByDescending(pair => pair.Value?.LastUpdatedAt ?? DateTimeOffset.MinValue)
                     .Take(MaximumChannels))
        {
            if (string.IsNullOrWhiteSpace(channelId) || channel is null ||
                channel.BannedUsers is null || channel.UnbanRequests is null)
            {
                throw new InvalidDataException("Moderation cache contains an invalid channel entry.");
            }

            if (channel.BannedUsers.Any(static item => item is null || string.IsNullOrWhiteSpace(item.UserId)) ||
                channel.UnbanRequests.Any(static item => item is null || string.IsNullOrWhiteSpace(item.RequestId)))
            {
                throw new InvalidDataException("Moderation cache contains an invalid moderation entry.");
            }

            if (channel.LastUpdatedAt < cutoff)
            {
                continue;
            }

            channel.LastUpdatedAt = channel.LastUpdatedAt > now ? now : channel.LastUpdatedAt;
            channel.BannedUsers = channel.BannedUsers
                .Where(item => item.ExpiresAt is null || item.ExpiresAt > now)
                .Take(MaximumBannedUsersPerChannel)
                .ToList();
            channel.UnbanRequests = channel.UnbanRequests
                .Where(item => item.Status == UnbanRequestStatus.Pending)
                .Take(MaximumRequestsPerChannel)
                .ToList();

            channels.Add(channelId, channel);
        }

        document.Channels = channels;
        return document;
    }

    private static void PreserveCorruptDocument()
    {
        try
        {
            if (!File.Exists(AppPaths.ModerationCacheFile))
            {
                return;
            }

            var suffix = DateTimeOffset.UtcNow.ToString(
                "yyyyMMdd-HHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture);
            File.Move(AppPaths.ModerationCacheFile, $"{AppPaths.ModerationCacheFile}.corrupt-{suffix}");
        }
        catch
        {
            // Preserve the original file in place if it cannot be quarantined.
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? pending;
        Task pendingTask;
        lock (_gate)
        {
            pending = _debounceCts;
            _debounceCts = null;
            pendingTask = _pendingSaveTask;
            _pendingSaveTask = Task.CompletedTask;
        }
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            await pendingTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Moderation cache pending save shutdown failed: {ex.GetType().Name}");
        }
        try
        {
            using var finalSaveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SaveAsync(finalSaveTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Moderation cache final save exceeded the shutdown deadline.");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Moderation cache final save failed: {ex.GetType().Name}");
        }
        _writeGate.Dispose();
    }

    public sealed class ModerationCacheDocument
    {
        public Dictionary<string, ModerationChannelCache> Channels { get; set; } = new(StringComparer.Ordinal);
        public ModerationCacheDocument Clone() => NormalizeAndValidate(
            JsonSerializer.Deserialize<ModerationCacheDocument>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions));
    }

    public sealed class ModerationChannelCache
    {
        public DateTimeOffset LastUpdatedAt { get; set; }
        public List<CachedBannedUser> BannedUsers { get; set; } = [];
        public List<CachedUnbanRequest> UnbanRequests { get; set; } = [];

        public static ModerationChannelCache FromSession(ChannelSessionViewModel session) => new()
        {
            LastUpdatedAt = DateTimeOffset.UtcNow,
            BannedUsers = session.BannedUsers.Take(MaximumBannedUsersPerChannel).Select(entry =>
            {
                session.ActivePunishments.TryGetValue(entry.UserId, out var state);
                return new CachedBannedUser
                {
                    UserId = entry.UserId,
                    UserLogin = entry.UserLogin,
                    DisplayName = entry.DisplayName,
                    CreatedAt = entry.CreatedAt,
                    ExpiresAt = entry.ExpiresAt,
                    Reason = entry.Reason,
                    ModeratorId = state?.ModeratorId ?? string.Empty,
                    ModeratorName = state?.ModeratorName ?? string.Empty,
                    LastConfirmedAt = state?.LastUpdatedAt ?? DateTimeOffset.UtcNow,
                    Source = state?.Source ?? PunishmentSource.LocalAction
                };
            }).ToList(),
            UnbanRequests = session.UnbanRequests.Take(MaximumRequestsPerChannel).Select(CachedUnbanRequest.FromModel).ToList()
        };
    }

    public sealed class CachedBannedUser
    {
        public string UserId { get; set; } = string.Empty;
        public string UserLogin { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string ModeratorId { get; set; } = string.Empty;
        public string ModeratorName { get; set; } = string.Empty;
        public DateTimeOffset LastConfirmedAt { get; set; }
        public PunishmentSource Source { get; set; }
    }

    public sealed class CachedUnbanRequest
    {
        public string RequestId { get; set; } = string.Empty;
        public string BroadcasterId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserLogin { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string RequestText { get; set; } = string.Empty;
        public UnbanRequestStatus Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string ResolutionText { get; set; } = string.Empty;
        public string ModeratorId { get; set; } = string.Empty;
        public string ModeratorName { get; set; } = string.Empty;

        public static CachedUnbanRequest FromModel(UnbanRequestEntry item) => new()
        {
            RequestId = item.RequestId,
            BroadcasterId = item.BroadcasterId,
            UserId = item.UserId,
            UserLogin = item.UserLogin,
            DisplayName = item.DisplayName,
            RequestText = item.RequestText,
            Status = item.Status,
            CreatedAt = item.CreatedAt,
            ResolvedAt = item.ResolvedAt,
            ResolutionText = item.ResolutionText,
            ModeratorId = item.ModeratorId,
            ModeratorName = item.ModeratorName
        };

        public UnbanRequestEntry ToModel() => new()
        {
            RequestId = RequestId,
            BroadcasterId = BroadcasterId,
            UserId = UserId,
            UserLogin = UserLogin,
            DisplayName = DisplayName,
            RequestText = RequestText,
            Status = Status,
            CreatedAt = CreatedAt,
            ResolvedAt = ResolvedAt,
            ResolutionText = ResolutionText,
            ModeratorId = ModeratorId,
            ModeratorName = ModeratorName
        };
    }
}
