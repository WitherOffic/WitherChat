using System.IO;
using System.Text.Json;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class TwitchBadgeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly TwitchApiClient _apiClient;
    private readonly FileLogger _logger;
    private readonly object _gate = new();
    private Dictionary<string, TwitchBadgeDefinition> _badges = new(StringComparer.OrdinalIgnoreCase);
    private bool _diskLoaded;

    public TwitchBadgeService(TwitchApiClient apiClient, FileLogger logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task RefreshAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        await LoadFromDiskAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var next = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var badge in await _apiClient.GetGlobalChatBadgesAsync(cancellationToken).ConfigureAwait(false))
            {
                next[CreateKey(badge.SetId, badge.Id)] = badge;
            }

            if (!string.IsNullOrWhiteSpace(broadcasterId))
            {
                foreach (var badge in await _apiClient.GetChannelChatBadgesAsync(broadcasterId, cancellationToken).ConfigureAwait(false))
                {
                    next[CreateKey(badge.SetId, badge.Id)] = badge;
                }
            }

            lock (_gate)
            {
                _badges = next;
            }

            await SaveToDiskAsync(next, broadcasterId, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Twitch badges cached: {next.Count}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Twitch badge refresh failed, using cached badges: {ex.GetType().Name}");
        }
    }

    public void ApplyBadgeImages(IEnumerable<BadgeModel> badges)
    {
        Dictionary<string, TwitchBadgeDefinition> snapshot;
        lock (_gate)
        {
            snapshot = _badges;
        }

        foreach (var badge in badges)
        {
            if (snapshot.TryGetValue(CreateKey(badge.SetId, badge.Id), out var definition))
            {
                badge.ImageUrl = FirstNonEmpty(definition.ImageUrl2x, definition.ImageUrl1x, definition.ImageUrl4x);
                badge.Title = definition.Title;
            }
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
            if (cache?.Badges is null)
            {
                return;
            }

            var loaded = new Dictionary<string, TwitchBadgeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var badge in cache.Badges)
            {
                if (!string.IsNullOrWhiteSpace(badge.SetId) && !string.IsNullOrWhiteSpace(badge.Id))
                {
                    loaded[CreateKey(badge.SetId, badge.Id)] = badge;
                }
            }

            lock (_gate)
            {
                _badges = loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Badge cache load failed: {ex.GetType().Name}");
        }
    }

    private static async Task SaveToDiskAsync(
        IReadOnlyDictionary<string, TwitchBadgeDefinition> badges,
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.LocalDataDirectory);
        var cache = new BadgeCacheDocument
        {
            BroadcasterId = broadcasterId,
            CachedAt = DateTimeOffset.UtcNow,
            Badges = badges.Values.ToList()
        };

        await using var stream = File.Create(AppPaths.BadgeCacheFile);
        await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

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
    }
}
