using System.Net;
using System.Net.Http;
using System.Text.Json;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class BttvEmoteProvider : IThirdPartyEmoteProvider
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.betterttv.net/3/"), Timeout = TimeSpan.FromSeconds(15) };
    private readonly FileLogger _logger;

    public BttvEmoteProvider(FileLogger logger)
    {
        _logger = logger;
    }

    public string Name => "BTTV";

    public async Task<IReadOnlyList<ThirdPartyEmote>> LoadEmotesAsync(string twitchBroadcasterId, CancellationToken cancellationToken = default)
    {
        var emotes = new List<ThirdPartyEmote>(256);

        using (var global = await TryGetJsonAsync("cached/emotes/global", cancellationToken).ConfigureAwait(false))
        {
            if (global?.RootElement.ValueKind == JsonValueKind.Array)
            {
                AddEmoteArray(global.RootElement, emotes);
            }
        }

        if (!string.IsNullOrWhiteSpace(twitchBroadcasterId))
        {
            var url = "cached/users/twitch/" + Uri.EscapeDataString(twitchBroadcasterId);
            using var channel = await TryGetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (channel is not null)
            {
                if (channel.RootElement.TryGetProperty("channelEmotes", out var channelEmotes) &&
                    channelEmotes.ValueKind == JsonValueKind.Array)
                {
                    AddEmoteArray(channelEmotes, emotes);
                }

                if (channel.RootElement.TryGetProperty("sharedEmotes", out var sharedEmotes) &&
                    sharedEmotes.ValueKind == JsonValueKind.Array)
                {
                    AddEmoteArray(sharedEmotes, emotes);
                }
            }
        }

        return emotes;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"BTTV emotes failed: {(int)response.StatusCode} {url}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"BTTV provider disabled for this refresh: {ex.GetType().Name}");
            return null;
        }
    }

    private static void AddEmoteArray(JsonElement array, ICollection<ThirdPartyEmote> emotes)
    {
        foreach (var item in array.EnumerateArray())
        {
            var id = GetString(item, "id");
            var code = GetString(item, "code");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            emotes.Add(new ThirdPartyEmote(id, code, $"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x", "BTTV"));
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }
}
