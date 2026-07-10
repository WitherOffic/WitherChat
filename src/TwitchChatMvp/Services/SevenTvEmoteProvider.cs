using System.Net;
using System.Net.Http;
using System.Text.Json;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class SevenTvEmoteProvider : IThirdPartyEmoteProvider
{
    private const int ZeroWidthFlag = 1 << 8;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://7tv.io/v3/"), Timeout = TimeSpan.FromSeconds(15) };
    private readonly FileLogger _logger;

    public SevenTvEmoteProvider(FileLogger logger)
    {
        _logger = logger;
    }

    public string Name => "7TV";

    public async Task<IReadOnlyList<ThirdPartyEmote>> LoadEmotesAsync(string twitchBroadcasterId, CancellationToken cancellationToken = default)
    {
        var emotes = new List<ThirdPartyEmote>(512);
        var globalCount = 0;

        using (var global = await TryGetJsonAsync("emote-sets/global", cancellationToken).ConfigureAwait(false))
        {
            if (global is not null &&
                global.RootElement.TryGetProperty("emotes", out var globalEmotes) &&
                globalEmotes.ValueKind == JsonValueKind.Array)
            {
                globalCount = AddEmoteArray(globalEmotes, emotes);
                _logger.Info($"7TV global status=200 set_id={GetString(global.RootElement, "id")} emotes={globalCount}");
            }
        }

        if (ulong.TryParse(twitchBroadcasterId, out _))
        {
            var url = "users/twitch/" + Uri.EscapeDataString(twitchBroadcasterId);
            using var channel = await TryGetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (channel is not null &&
                channel.RootElement.TryGetProperty("emote_set", out var emoteSet) &&
                emoteSet.ValueKind == JsonValueKind.Object &&
                emoteSet.TryGetProperty("emotes", out var channelEmotes) &&
                channelEmotes.ValueKind == JsonValueKind.Array)
            {
                var names = channelEmotes.EnumerateArray()
                    .Select(item => GetString(item, "name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToArray();
                var channelCount = AddEmoteArray(channelEmotes, emotes);
                var sample = string.Join(',', names.Take(5));
                _logger.Info(
                    $"7TV channel status=200 broadcaster_id={twitchBroadcasterId} " +
                    $"set_id={GetString(emoteSet, "id")} emotes={channelCount} sample={sample} " +
                    $"tokens[ф={names.Contains("ф", StringComparer.Ordinal)},0={names.Contains("0", StringComparer.Ordinal)},f={names.Contains("f", StringComparer.Ordinal)}]");
            }
            else if (channel is null)
            {
                _logger.Info($"7TV channel unavailable broadcaster_id={twitchBroadcasterId}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(twitchBroadcasterId))
        {
            _logger.Warn("7TV channel lookup skipped: broadcaster ID is not numeric.");
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
                _logger.Info($"7TV status=404 endpoint={url}");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"7TV emotes failed: {(int)response.StatusCode} {url}");
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
            _logger.Warn($"7TV provider disabled for this refresh: {ex.GetType().Name}");
            return null;
        }
    }

    private static int AddEmoteArray(JsonElement array, ICollection<ThirdPartyEmote> emotes)
    {
        var count = 0;
        foreach (var item in array.EnumerateArray())
        {
            var code = GetString(item, "name");
            var id = GetString(item, "id");
            string imageUrl = string.Empty;
            string fallbackImageUrl = string.Empty;
            var isZeroWidth = false;

            if (item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var dataName = GetString(data, "name");
                if (string.IsNullOrWhiteSpace(code))
                {
                    code = dataName;
                }

                var dataId = GetString(data, "id");
                if (!string.IsNullOrWhiteSpace(dataId))
                {
                    id = dataId;
                }

                (imageUrl, fallbackImageUrl) = TryGetHostImageUrls(data);
                isZeroWidth = (GetInt32(data, "flags") & ZeroWidthFlag) != 0;
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                imageUrl = $"https://cdn.7tv.app/emote/{Uri.EscapeDataString(id)}/2x.webp";
                fallbackImageUrl = $"https://cdn.7tv.app/emote/{Uri.EscapeDataString(id)}/2x.png";
            }

            var names = new HashSet<string>(StringComparer.Ordinal) { code };
            if (item.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (var alias in aliases.EnumerateArray())
                {
                    if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                    {
                        names.Add(alias.GetString()!);
                    }
                }
            }

            foreach (var name in names)
            {
                emotes.Add(new ThirdPartyEmote(id, name, imageUrl, "7TV", fallbackImageUrl, isZeroWidth));
            }

            count++;
        }

        return count;
    }

    private static (string ImageUrl, string FallbackImageUrl) TryGetHostImageUrls(JsonElement data)
    {
        if (!data.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty);
        }

        var hostUrl = GetString(host, "url");
        if (string.IsNullOrWhiteSpace(hostUrl))
        {
            return (string.Empty, string.Empty);
        }

        if (hostUrl.StartsWith("//", StringComparison.Ordinal))
        {
            hostUrl = "https:" + hostUrl;
        }
        else if (hostUrl.StartsWith("/", StringComparison.Ordinal))
        {
            hostUrl = "https://cdn.7tv.app" + hostUrl;
        }

        if (host.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            var names = files.EnumerateArray()
                .Select(file => GetString(file, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preferred = new[] { "2x.gif", "2x.webp", "2x.avif", "2x.png" }.FirstOrDefault(names.Contains);
            var fallback = new[] { "2x.png", "1x.png" }.FirstOrDefault(names.Contains);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return (
                    hostUrl.TrimEnd('/') + "/" + preferred,
                    string.IsNullOrWhiteSpace(fallback) ? string.Empty : hostUrl.TrimEnd('/') + "/" + fallback);
            }
        }

        return (hostUrl.TrimEnd('/') + "/2x.webp", hostUrl.TrimEnd('/') + "/2x.png");
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
    }
}
