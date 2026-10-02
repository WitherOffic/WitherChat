using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed record ModerationCacheSnapshot(
    IReadOnlyList<BannedUser> BannedUsers,
    IReadOnlyList<UnbanRequest> UnbanRequests);

public sealed class ModerationCacheStore : IAsyncDisposable
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const int MaximumChannels = 32;
    private const int MaximumBannedUsers = 1000;
    private const int MaximumUnbanRequests = 500;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WitherChat.ModerationCache.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppDataPaths _paths;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CacheDocument _document;
    private CancellationTokenSource? _debounceCancellation;
    private Task _pendingSave = Task.CompletedTask;
    private bool _disposed;

    public ModerationCacheStore(AppDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _document = Load();
    }

    public ModerationCacheSnapshot Restore(string broadcasterId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return new ModerationCacheSnapshot([], []);
        }

        lock (_gate)
        {
            if (!_document.Channels.TryGetValue(broadcasterId, out var cached))
            {
                return new ModerationCacheSnapshot([], []);
            }

            var now = DateTimeOffset.UtcNow;
            return new ModerationCacheSnapshot(
                cached.BannedUsers
                    .Where(item => item.ExpiresAt is null || item.ExpiresAt > now)
                    .Take(MaximumBannedUsers)
                    .Select(item => item.ToModel())
                    .ToArray(),
                cached.UnbanRequests
                    .Where(item => item.Status == UnbanRequestStatus.Pending)
                    .Take(MaximumUnbanRequests)
                    .Select(item => item.ToModel())
                    .ToArray());
        }
    }

    public void ScheduleSave(
        string broadcasterId,
        IEnumerable<BannedUser> bannedUsers,
        IEnumerable<UnbanRequest> unbanRequests)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(bannedUsers);
        ArgumentNullException.ThrowIfNull(unbanRequests);
        lock (_gate)
        {
            _document.Channels[broadcasterId] = new CacheChannel
            {
                LastUpdatedAt = DateTimeOffset.UtcNow,
                BannedUsers = bannedUsers
                    .Take(MaximumBannedUsers)
                    .Select(CachedBannedUser.FromModel)
                    .ToList(),
                UnbanRequests = unbanRequests
                    .Where(item => item.Status == UnbanRequestStatus.Pending)
                    .Take(MaximumUnbanRequests)
                    .Select(CachedUnbanRequest.FromModel)
                    .ToList()
            };
            TrimChannels(_document);
            _debounceCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _debounceCancellation = cancellation;
            _pendingSave = SaveAfterDelayAsync(cancellation);
        }
    }

    private async Task SaveAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(350, cancellation.Token).ConfigureAwait(false);
            await SaveAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_debounceCancellation, cancellation))
                {
                    _debounceCancellation = null;
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
            CacheDocument snapshot;
            lock (_gate)
            {
                snapshot = Clone(_document);
            }

            _paths.EnsureCreated();
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            var bytes = OperatingSystem.IsWindows()
                ? LocalDataProtection.Protect(json, Entropy)
                : json;
            var temporaryFile = _paths.ModerationCacheFile + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryFile, bytes, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporaryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                File.Move(temporaryFile, _paths.ModerationCacheFile, true);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryFile);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private CacheDocument Load()
    {
        var path = File.Exists(_paths.ModerationCacheFile)
            ? _paths.ModerationCacheFile
            : OperatingSystem.IsWindows() && File.Exists(_paths.LegacyModerationCacheFile)
                ? _paths.LegacyModerationCacheFile
                : null;
        if (path is null)
        {
            return new CacheDocument();
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumFileBytes)
            {
                return new CacheDocument();
            }

            var stored = File.ReadAllBytes(info.FullName);
            var json = LooksLikeJson(stored) || !OperatingSystem.IsWindows()
                ? stored
                : LocalDataProtection.Unprotect(stored, Entropy);
            var document = JsonSerializer.Deserialize<CacheDocument>(json, JsonOptions);
            return Normalize(document);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or
            System.ComponentModel.Win32Exception)
        {
            return new CacheDocument();
        }
    }

    private static CacheDocument Normalize(CacheDocument? document)
    {
        if (document?.Channels is null)
        {
            return new CacheDocument();
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - Retention;
        var normalized = new CacheDocument();
        foreach (var (key, value) in document.Channels
                     .Where(pair => pair.Value is not null && pair.Value.LastUpdatedAt >= cutoff)
                     .OrderByDescending(pair => pair.Value.LastUpdatedAt)
                     .Take(MaximumChannels))
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized.Channels[key] = new CacheChannel
            {
                LastUpdatedAt = value.LastUpdatedAt > now ? now : value.LastUpdatedAt,
                BannedUsers = (value.BannedUsers ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.UserId) &&
                                   (item.ExpiresAt is null || item.ExpiresAt > now))
                    .Take(MaximumBannedUsers)
                    .ToList(),
                UnbanRequests = (value.UnbanRequests ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.RequestId) &&
                                   item.Status == UnbanRequestStatus.Pending)
                    .Take(MaximumUnbanRequests)
                    .ToList()
            };
        }
        return normalized;
    }

    private static void TrimChannels(CacheDocument document)
    {
        while (document.Channels.Count > MaximumChannels)
        {
            var oldest = document.Channels.MinBy(pair => pair.Value.LastUpdatedAt);
            _ = document.Channels.Remove(oldest.Key);
        }
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> bytes)
    {
        var index = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        while (index < bytes.Length && bytes[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }
        return index < bytes.Length && bytes[index] == (byte)'{';
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static CacheDocument Clone(CacheDocument document) =>
        JsonSerializer.Deserialize<CacheDocument>(
            JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions),
            JsonOptions) ?? new CacheDocument();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        Task pending;
        lock (_gate)
        {
            _debounceCancellation?.Cancel();
            pending = _pendingSave;
        }
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        _writeGate.Dispose();
    }

    public sealed class CacheDocument
    {
        public Dictionary<string, CacheChannel> Channels { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class CacheChannel
    {
        public DateTimeOffset LastUpdatedAt { get; set; }
        public List<CachedBannedUser> BannedUsers { get; set; } = [];
        public List<CachedUnbanRequest> UnbanRequests { get; set; } = [];
    }

    public sealed class CachedBannedUser
    {
        public string UserId { get; set; } = string.Empty;
        public string UserLogin { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string Reason { get; set; } = string.Empty;

        public static CachedBannedUser FromModel(BannedUser item) => new()
        {
            UserId = item.UserId,
            UserLogin = item.UserLogin,
            DisplayName = item.DisplayName,
            CreatedAt = item.CreatedAt,
            ExpiresAt = item.ExpiresAt,
            Reason = item.Reason
        };

        public BannedUser ToModel() =>
            new(UserId, UserLogin, DisplayName, CreatedAt, ExpiresAt, Reason);
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

        public static CachedUnbanRequest FromModel(UnbanRequest item) => new()
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
            ResolutionText = item.ResolutionText
        };

        public UnbanRequest ToModel() => new(
            RequestId,
            BroadcasterId,
            UserId,
            UserLogin,
            DisplayName,
            RequestText,
            CreatedAt,
            Status,
            ResolvedAt,
            ResolutionText);
    }
}
