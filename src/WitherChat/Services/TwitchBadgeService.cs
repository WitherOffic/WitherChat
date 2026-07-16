using System.IO;
using System.Diagnostics;
using System.Text.Json;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class TwitchBadgeService : IDisposable
{
    private const int MaxChannelCatalogs = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly TwitchApiClient _apiClient;
    private readonly FileLogger _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<string, TwitchBadgeDefinition> _globalBadges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, TwitchBadgeDefinition>> _channelBadges = new(StringComparer.Ordinal);
    private bool _diskLoaded;

    public TwitchBadgeService(TwitchApiClient apiClient, FileLogger logger)
    {
        _apiClient = apiClient;
        _logger = logger;
#if DEBUG
        RunIsolationSelfTest();
#endif
    }

    public void Dispose() => _refreshGate.Dispose();

    public Task<BadgeCatalogSnapshot> EnsureChannelCatalogAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return Task.FromResult(BadgeCatalogSnapshot.Empty);
        }

        lock (_gate)
        {
            if (_channelBadges.TryGetValue(broadcasterId, out var cached))
            {
                return Task.FromResult(CreateSnapshot(cached));
            }
        }

        return RefreshAsync(broadcasterId, cancellationToken);
    }

    public async Task<BadgeCatalogSnapshot> RefreshAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(broadcasterId))
        {
            return BadgeCatalogSnapshot.Empty;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadFromDiskAsync(cancellationToken).ConfigureAwait(false);

            var shouldLoadGlobal = false;
            lock (_gate)
            {
                shouldLoadGlobal = _globalBadges.Count == 0;
            }

            if (shouldLoadGlobal)
            {
                try
                {
                    var global = CreateCatalog(await _apiClient
                        .GetGlobalChatBadgesAsync(cancellationToken)
                        .ConfigureAwait(false));
                    lock (_gate)
                    {
                        _globalBadges = global;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Global Twitch badge refresh failed: {ex.GetType().Name}");
                }
            }

            Dictionary<string, TwitchBadgeDefinition> channelCatalog;
            try
            {
                channelCatalog = CreateCatalog(await _apiClient
                    .GetChannelChatBadgesAsync(broadcasterId, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Channel Twitch badge refresh failed: broadcaster_id={broadcasterId}, {ex.GetType().Name}");
                lock (_gate)
                {
                    return _channelBadges.TryGetValue(broadcasterId, out var cached)
                        ? CreateSnapshot(cached, ex.GetType().Name)
                        : new BadgeCatalogSnapshot(0, 0, ex.GetType().Name);
                }
            }

            BadgeCatalogSnapshot snapshot;
            lock (_gate)
            {
                _channelBadges[broadcasterId] = channelCatalog;
                TrimChannelCatalogs(broadcasterId);
                snapshot = CreateSnapshot(channelCatalog);
            }

            await SaveToDiskAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void ApplyBadgeImages(string broadcasterId, IEnumerable<BadgeModel> badges)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        Dictionary<string, TwitchBadgeDefinition> globalSnapshot;
        Dictionary<string, TwitchBadgeDefinition>? channelSnapshot;
        lock (_gate)
        {
            globalSnapshot = _globalBadges;
            _channelBadges.TryGetValue(broadcasterId, out channelSnapshot);
        }

        foreach (var badge in badges)
        {
            var definition = ResolveDefinition(
                globalSnapshot,
                channelSnapshot,
                badge.SetId,
                badge.Id);
            var imageUrl = definition is null
                ? string.Empty
                : FirstNonEmpty(definition.ImageUrl2x, definition.ImageUrl1x, definition.ImageUrl4x);
            if (!string.Equals(badge.ImageUrl, imageUrl, StringComparison.Ordinal))
            {
                badge.ImageSource = null;
            }

            badge.ImageUrl = imageUrl;
            badge.Title = definition?.Title ?? string.Empty;
        }
    }

    public bool HasChannelCatalog(string broadcasterId)
    {
        lock (_gate)
        {
            return _channelBadges.ContainsKey((broadcasterId ?? string.Empty).Trim());
        }
    }

    private async Task LoadFromDiskAsync(CancellationToken cancellationToken)
    {
        if (_diskLoaded)
        {
            return;
        }

        _diskLoaded = true;
        try
        {
            if (!File.Exists(AppPaths.BadgeCacheFile))
            {
                return;
            }

            await using var stream = File.OpenRead(AppPaths.BadgeCacheFile);
            var cache = await JsonSerializer.DeserializeAsync<BadgeCacheDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (cache is null)
            {
                return;
            }

            lock (_gate)
            {
                _globalBadges = CreateCatalog(cache.GlobalBadges ?? []);
                foreach (var (broadcasterId, badges) in cache.ChannelBadges ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(broadcasterId))
                    {
                        _channelBadges[broadcasterId] = CreateCatalog(badges ?? []);
                    }
                }

                // Version 1 stored one merged global+channel catalog. Keep it scoped
                // only to the broadcaster it was created for so it cannot leak to others.
                if (_channelBadges.Count == 0 &&
                    !string.IsNullOrWhiteSpace(cache.BroadcasterId) &&
                    cache.Badges is { Count: > 0 })
                {
                    _channelBadges[cache.BroadcasterId] = CreateCatalog(cache.Badges);
                }

                TrimChannelCatalogs(cache.BroadcasterId);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Badge cache load failed: {ex.GetType().Name}");
        }
    }

    private async Task SaveToDiskAsync(CancellationToken cancellationToken)
    {
        BadgeCacheDocument cache;
        lock (_gate)
        {
            cache = new BadgeCacheDocument
            {
                CachedAt = DateTimeOffset.UtcNow,
                GlobalBadges = _globalBadges.Values.ToList(),
                ChannelBadges = _channelBadges.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Values.ToList(),
                    StringComparer.Ordinal)
            };
        }

        Directory.CreateDirectory(AppPaths.LocalDataDirectory);
        var tempPath = AppPaths.BadgeCacheFile + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, AppPaths.BadgeCacheFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void TrimChannelCatalogs(string keepBroadcasterId)
    {
        while (_channelBadges.Count > MaxChannelCatalogs)
        {
            var removable = _channelBadges.Keys.FirstOrDefault(key =>
                !string.Equals(key, keepBroadcasterId, StringComparison.Ordinal));
            if (removable is null)
            {
                break;
            }

            _channelBadges.Remove(removable);
        }
    }

    private static Dictionary<string, TwitchBadgeDefinition> CreateCatalog(
        IEnumerable<TwitchBadgeDefinition> badges)
    {
        var catalog = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var badge in badges)
        {
            if (!string.IsNullOrWhiteSpace(badge.SetId) && !string.IsNullOrWhiteSpace(badge.Id))
            {
                catalog[CreateKey(badge.SetId, badge.Id)] = badge;
            }
        }

        return catalog;
    }

    private static BadgeCatalogSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, TwitchBadgeDefinition> catalog,
        string error = "") =>
        new(
            catalog.Values.Select(badge => badge.SetId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            catalog.Count,
            error);

    private static TwitchBadgeDefinition? ResolveDefinition(
        IReadOnlyDictionary<string, TwitchBadgeDefinition> globalCatalog,
        IReadOnlyDictionary<string, TwitchBadgeDefinition>? channelCatalog,
        string setId,
        string versionId)
    {
        var key = CreateKey(setId, versionId);
        if (channelCatalog?.TryGetValue(key, out var channelBadge) == true)
        {
            return channelBadge;
        }

        return globalCatalog.TryGetValue(key, out var globalBadge) ? globalBadge : null;
    }

#if DEBUG
    private static void RunIsolationSelfTest()
    {
        static TwitchBadgeDefinition Badge(string url) => new("subscriber", "1", url, url, url, url);
        var global = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [CreateKey("moderator", "1")] = new("moderator", "1", "global", "global", "global", "global")
        };
        var channelA = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [CreateKey("subscriber", "1")] = Badge("url-A")
        };
        var channelB = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [CreateKey("subscriber", "1")] = Badge("url-B")
        };
        var channelC = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [CreateKey("subscriber", "1")] = Badge("url-C")
        };

        if (ResolveDefinition(global, channelA, "subscriber", "1")?.ImageUrl2x != "url-A" ||
            ResolveDefinition(global, channelB, "subscriber", "1")?.ImageUrl2x != "url-B" ||
            ResolveDefinition(global, channelC, "subscriber", "1")?.ImageUrl2x != "url-C" ||
            ResolveDefinition(global, channelA, "moderator", "1")?.ImageUrl2x != "global" ||
            ResolveDefinition(global, null, "subscriber", "1") is not null)
        {
            throw new InvalidOperationException("Twitch badge channel-isolation self-test failed.");
        }

        Debug.WriteLine("Twitch badge channel-isolation self-test passed: 3 broadcaster catalogs.");
    }
#endif

    private static string CreateKey(string setId, string id) => $"{setId}:{id}";

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private sealed class BadgeCacheDocument
    {
        public string BroadcasterId { get; set; } = string.Empty;
        public DateTimeOffset CachedAt { get; set; }
        public List<TwitchBadgeDefinition> Badges { get; set; } = [];
        public List<TwitchBadgeDefinition> GlobalBadges { get; set; } = [];
        public Dictionary<string, List<TwitchBadgeDefinition>> ChannelBadges { get; set; } = new(StringComparer.Ordinal);
    }
}

public sealed record BadgeCatalogSnapshot(
    int SetCount,
    int VersionCount,
    string Error)
{
    public static BadgeCatalogSnapshot Empty { get; } = new(
        0,
        0,
        string.Empty);
}
