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
            var next = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal);

            if (enableBttv)
            {
                await LoadProviderAsync(_bttvProvider, twitchBroadcasterId, next, cancellationToken).ConfigureAwait(false);
            }

            if (enableSevenTv)
            {
                await LoadProviderAsync(_sevenTvProvider, twitchBroadcasterId, next, cancellationToken).ConfigureAwait(false);
            }

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

    private async Task LoadProviderAsync(
        IThirdPartyEmoteProvider provider,
        string twitchBroadcasterId,
        IDictionary<string, ThirdPartyEmote> target,
        CancellationToken cancellationToken)
    {
        try
        {
            var emotes = await provider.LoadEmotesAsync(twitchBroadcasterId, cancellationToken).ConfigureAwait(false);
            foreach (var emote in emotes)
            {
                if (!string.IsNullOrWhiteSpace(emote.Code) && !string.IsNullOrWhiteSpace(emote.ImageUrl))
                {
                    target[emote.Code] = emote;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"{provider.Name} emotes skipped: {ex.GetType().Name}");
        }
    }

    private static string NormalizeChannelKey(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
