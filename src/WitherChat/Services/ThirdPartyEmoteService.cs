using WitherChat.Models;

namespace WitherChat.Services;

public sealed class ThirdPartyEmoteService : IDisposable
{
    private readonly FileLogger _logger;
    private readonly BttvEmoteProvider _bttvProvider;
    private readonly SevenTvEmoteProvider _sevenTvProvider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, ThirdPartyEmote>> _emotesByChannel = new(StringComparer.OrdinalIgnoreCase);
    private string _activeChannelKey = string.Empty;

    public ThirdPartyEmoteService(FileLogger logger)
    {
#if DEBUG
        ThirdPartyEmoteTokenizer.RunSelfTests();
#endif
        _logger = logger;
        _bttvProvider = new BttvEmoteProvider(logger);
        _sevenTvProvider = new SevenTvEmoteProvider(logger);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _emotesByChannel.TryGetValue(_activeChannelKey, out var emotes) ? emotes.Count : 0;
            }
        }
    }

    public async Task RefreshAsync(
        string twitchBroadcasterId,
        bool enableBttv,
        bool enableSevenTv,
        CancellationToken cancellationToken = default)
    {
        var channelKey = NormalizeChannelKey(twitchBroadcasterId);
        if (string.IsNullOrWhiteSpace(channelKey))
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bttvTask = enableBttv
                ? LoadProviderAsync(_bttvProvider, twitchBroadcasterId, cancellationToken)
                : Task.FromResult<IReadOnlyList<ThirdPartyEmote>>([]);
            var sevenTvTask = enableSevenTv
                ? LoadProviderAsync(_sevenTvProvider, twitchBroadcasterId, cancellationToken)
                : Task.FromResult<IReadOnlyList<ThirdPartyEmote>>([]);

            await Task.WhenAll(bttvTask, sevenTvTask).ConfigureAwait(false);
            var next = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal);
            AddEmotes(next, await bttvTask.ConfigureAwait(false));
            AddEmotes(next, await sevenTvTask.ConfigureAwait(false));

            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _emotesByChannel[channelKey] = next;
                if (string.IsNullOrWhiteSpace(_activeChannelKey))
                {
                    _activeChannelKey = channelKey;
                }

                while (_emotesByChannel.Count > 3)
                {
                    var removable = _emotesByChannel.Keys.FirstOrDefault(key =>
                        !string.Equals(key, _activeChannelKey, StringComparison.OrdinalIgnoreCase));
                    if (removable is null)
                    {
                        break;
                    }

                    _emotesByChannel.Remove(removable);
                }
            }

            _logger.Info($"Third-party emotes cached: channel={channelKey}, count={next.Count}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public bool TryGetEmote(string code, out ThirdPartyEmote emote)
    {
        return TryGetEmote(_activeChannelKey, code, out emote);
    }

    public bool TryGetEmote(string channelKey, string code, out ThirdPartyEmote emote)
    {
        lock (_gate)
        {
            if (_emotesByChannel.TryGetValue(NormalizeChannelKey(channelKey), out var emotes) &&
                emotes.TryGetValue(code, out emote!))
            {
                return true;
            }

            emote = null!;
            return false;
        }
    }

    public void SetActiveChannel(string channelKey)
    {
        lock (_gate)
        {
            _activeChannelKey = NormalizeChannelKey(channelKey);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _emotesByChannel.Clear();
            _activeChannelKey = string.Empty;
        }
    }

    public void Clear(string channelKey)
    {
        lock (_gate)
        {
            _emotesByChannel.Remove(NormalizeChannelKey(channelKey));
        }
    }

    public void Dispose()
    {
        _bttvProvider.Dispose();
        _sevenTvProvider.Dispose();
        _refreshGate.Dispose();
    }

    private async Task<IReadOnlyList<ThirdPartyEmote>> LoadProviderAsync(
        IThirdPartyEmoteProvider provider,
        string twitchBroadcasterId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.LoadEmotesAsync(twitchBroadcasterId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{provider.Name} emotes skipped: {ex.GetType().Name}");
            return [];
        }
    }

    private static void AddEmotes(
        IDictionary<string, ThirdPartyEmote> target,
        IEnumerable<ThirdPartyEmote> emotes)
    {
        foreach (var emote in emotes)
        {
            if (!string.IsNullOrWhiteSpace(emote.Code) && !string.IsNullOrWhiteSpace(emote.ImageUrl))
            {
                target[emote.Code] = emote;
            }
        }
    }

    private static string NormalizeChannelKey(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
