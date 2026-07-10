using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class ThirdPartyEmoteService
{
    private readonly FileLogger _logger;
    private readonly BttvEmoteProvider _bttvProvider;
    private readonly SevenTvEmoteProvider _sevenTvProvider;
    private readonly object _gate = new();
    private Dictionary<string, ThirdPartyEmote> _emotes = new(StringComparer.Ordinal);

    public ThirdPartyEmoteService(FileLogger logger)
    {
        ThirdPartyEmoteTokenizer.RunSelfTests();
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
                return _emotes.Count;
            }
        }
    }

    public async Task RefreshAsync(
        string twitchBroadcasterId,
        bool enableBttv,
        bool enableSevenTv,
        CancellationToken cancellationToken = default)
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
            _emotes = next;
        }

        _logger.Info($"Third-party emotes cached: {next.Count}");
    }

    public bool TryGetEmote(string code, out ThirdPartyEmote emote)
    {
        lock (_gate)
        {
            return _emotes.TryGetValue(code, out emote!);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _emotes = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal);
        }
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
}
