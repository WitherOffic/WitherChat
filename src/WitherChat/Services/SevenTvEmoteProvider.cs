using System.Net;
using System.Net.Http;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class SevenTvEmoteProvider : IThirdPartyEmoteProvider, IDisposable
{
    [Flags]
    private enum SevenTvEmoteFlags
    {
        None = 0,
        ZeroWidth = 1 << 8
    }
    private const int MaxResponseBytes = 6 * 1024 * 1024;
    private readonly HttpClient _http = new(new HttpClientHandler { CheckCertificateRevocationList = true })
    {
        BaseAddress = new Uri("https://7tv.io/v3/"),
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly FileLogger _logger;

    public SevenTvEmoteProvider(FileLogger logger)
    {
        _logger = logger;
    }

    public string Name => "7TV";

    public void Dispose() => _http.Dispose();

    public async Task<IReadOnlyList<ThirdPartyEmote>> LoadEmotesAsync(string twitchBroadcasterId, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var emotes = new List<ThirdPartyEmote>(512);
        var globalCount = 0;
        var responseBytes = 0;
        var channelSetId = string.Empty;
        var channelCount = 0;
        var error = "none";

        using (var global = await TryGetJsonAsync("emote-sets/global", cancellationToken).ConfigureAwait(false))
        {
            if (global is not null &&
                global.Document.RootElement.TryGetProperty("emotes", out var globalEmotes) &&
                globalEmotes.ValueKind == JsonValueKind.Array)
            {
                responseBytes += global.BytesRead;
                globalCount = AddEmoteArray(globalEmotes, emotes);
            }
        }

        if (ulong.TryParse(twitchBroadcasterId, out _))
        {
            var url = "users/twitch/" + Uri.EscapeDataString(twitchBroadcasterId);
            using var channel = await TryGetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (channel is not null &&
                channel.Document.RootElement.TryGetProperty("emote_set", out var emoteSet) &&
                emoteSet.ValueKind == JsonValueKind.Object &&
                emoteSet.TryGetProperty("emotes", out var channelEmotes) &&
                channelEmotes.ValueKind == JsonValueKind.Array)
            {
                responseBytes += channel.BytesRead;
                channelSetId = GetString(emoteSet, "id");
                channelCount = AddEmoteArray(channelEmotes, emotes);
            }
            else if (channel is null)
            {
                error = "channel-unavailable";
            }
        }
        else if (!string.IsNullOrWhiteSpace(twitchBroadcasterId))
        {
            error = "invalid-broadcaster-id";
        }

        _logger.Info(
            $"7TV loaded: broadcasterId={twitchBroadcasterId}, setId={channelSetId}, " +
            $"emotes={channelCount}, global={globalCount}, bytes={responseBytes}, " +
            $"elapsedMs={stopwatch.ElapsedMilliseconds}, error={error}");

        return emotes;
    }

    private async Task<JsonPayload?> TryGetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
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

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"7TV emotes failed: unexpected content type {mediaType ?? "empty"}");
                return null;
            }

            return await ReadBoundedJsonAsync(response, cancellationToken).ConfigureAwait(false);
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

    private static async Task<JsonPayload> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("7TV response exceeds the size limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("7TV response exceeds the size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        output.Position = 0;
        var bytesRead = checked((int)output.Length);
        var document = await JsonDocument.ParseAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new JsonPayload(document, bytesRead);
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
            var isAnimated = false;
            var flags = GetInt32(item, "flags");
            var sourceWidth = 0;
            var sourceHeight = 0;

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

                flags |= GetInt32(data, "flags");
                (imageUrl, fallbackImageUrl, isAnimated, sourceWidth, sourceHeight) = TryGetHostImageUrls(data);
                isZeroWidth = ((SevenTvEmoteFlags)flags & SevenTvEmoteFlags.ZeroWidth) != 0;
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                imageUrl = $"https://cdn.7tv.app/emote/{Uri.EscapeDataString(id)}/2x.png";
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
                emotes.Add(new ThirdPartyEmote(
                    id,
                    name,
                    imageUrl,
                    "7TV",
                    fallbackImageUrl,
                    isZeroWidth,
                    isAnimated,
                    flags,
                    sourceWidth,
                    sourceHeight));
            }

            count++;
        }

        return count;
    }

    private static (string ImageUrl, string FallbackImageUrl, bool IsAnimated, int Width, int Height) TryGetHostImageUrls(JsonElement data)
    {
        if (!data.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty, false, 0, 0);
        }

        var hostUrl = GetString(host, "url");
        if (string.IsNullOrWhiteSpace(hostUrl))
        {
            return (string.Empty, string.Empty, false, 0, 0);
        }

        if (hostUrl.StartsWith("//", StringComparison.Ordinal))
        {
            hostUrl = "https:" + hostUrl;
        }
        else if (hostUrl.StartsWith('/'))
        {
            hostUrl = "https://cdn.7tv.app" + hostUrl;
        }

        if (host.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            var descriptors = files.EnumerateArray()
                .Select(file => new SevenTvFile(
                    GetString(file, "name"),
                    GetInt32(file, "width"),
                    GetInt32(file, "height")))
                .Where(file => !string.IsNullOrWhiteSpace(file.Name))
                .ToArray();
            var animated = FindFile(descriptors, "1x.gif", "2x.gif");
            var preferred = animated ?? FindFile(descriptors, "2x.png", "1x.png");
            var fallback = FindFile(descriptors, "2x.png", "1x.png");

            if (preferred is not null)
            {
                return (
                    hostUrl.TrimEnd('/') + "/" + preferred.Name,
                    fallback is null ? string.Empty : hostUrl.TrimEnd('/') + "/" + fallback.Name,
                    animated is not null,
                    preferred.Width,
                    preferred.Height);
            }
        }

        return (string.Empty, string.Empty, false, 0, 0);
    }

    private static SevenTvFile? FindFile(IEnumerable<SevenTvFile> files, params string[] names)
    {
        foreach (var name in names)
        {
            var match = files.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
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

    private sealed record JsonPayload(JsonDocument Document, int BytesRead) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private sealed record SevenTvFile(string Name, int Width, int Height);
}
