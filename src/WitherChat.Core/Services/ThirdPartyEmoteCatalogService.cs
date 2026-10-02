using System.Net;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed record ThirdPartyEmoteCatalogLoadResult(
    IReadOnlyDictionary<string, ThirdPartyEmote> Catalog,
    IReadOnlySet<string> FailedProviders)
{
    public bool IsComplete => FailedProviders.Count == 0;
}

public sealed class ThirdPartyEmoteCatalogService : IDisposable
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;

    public ThirdPartyEmoteCatalogService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null
            ? new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    CheckCertificateRevocationList = true
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<IReadOnlyDictionary<string, ThirdPartyEmote>> LoadAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        var load = await LoadDetailedAsync(broadcasterId, cancellationToken).ConfigureAwait(false);
        if (!load.IsComplete)
        {
            throw new HttpRequestException(
                "Third-party emote providers are temporarily unavailable: " +
                string.Join(", ", load.FailedProviders));
        }
        return load.Catalog;
    }

    public async Task<ThirdPartyEmoteCatalogLoadResult> LoadDetailedAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        broadcasterId = (broadcasterId ?? string.Empty).Trim();
        var tasks = new List<Task<SourceLoadResult>>
        {
            CaptureSourceAsync(
                ThirdPartyEmoteProviders.Bttv,
                () => LoadBttvGlobalAsync(cancellationToken),
                cancellationToken),
            CaptureSourceAsync(
                ThirdPartyEmoteProviders.SevenTv,
                () => LoadSevenTvGlobalAsync(cancellationToken),
                cancellationToken)
        };
        if (ulong.TryParse(broadcasterId, out _))
        {
            tasks.Add(CaptureSourceAsync(
                ThirdPartyEmoteProviders.Bttv,
                () => LoadBttvChannelAsync(broadcasterId, cancellationToken),
                cancellationToken));
            tasks.Add(CaptureSourceAsync(
                ThirdPartyEmoteProviders.SevenTv,
                () => LoadSevenTvChannelAsync(broadcasterId, cancellationToken),
                cancellationToken));
        }

        var catalogs = await Task.WhenAll(tasks).ConfigureAwait(false);
        var result = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal);
        var failedProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in catalogs)
        {
            if (!source.Succeeded)
            {
                failedProviders.Add(source.Provider);
            }
            foreach (var emote in source.Emotes)
            {
                if (!string.IsNullOrWhiteSpace(emote.Code))
                {
                    result[emote.Code] = emote;
                }
            }
        }

        return new ThirdPartyEmoteCatalogLoadResult(result, failedProviders);
    }

    public void Dispose() => _httpClient.Dispose();

    private static async Task<SourceLoadResult> CaptureSourceAsync(
        string provider,
        Func<Task<IReadOnlyList<ThirdPartyEmote>>> load,
        CancellationToken cancellationToken)
    {
        try
        {
            return new SourceLoadResult(provider, await load().ConfigureAwait(false), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return new SourceLoadResult(provider, [], false);
        }
    }

    private async Task<IReadOnlyList<ThirdPartyEmote>> LoadBttvGlobalAsync(CancellationToken cancellationToken)
    {
        using var document = await TryGetJsonAsync(
            "https://api.betterttv.net/3/cached/emotes/global",
            cancellationToken).ConfigureAwait(false);
        return document is { RootElement.ValueKind: JsonValueKind.Array }
            ? ParseBttvArray(document.RootElement)
            : [];
    }

    private async Task<IReadOnlyList<ThirdPartyEmote>> LoadBttvChannelAsync(
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        using var document = await TryGetJsonAsync(
            "https://api.betterttv.net/3/cached/users/twitch/" + Uri.EscapeDataString(broadcasterId),
            cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        var result = new List<ThirdPartyEmote>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The BTTV channel response has an invalid shape.");
        }

        if (document.RootElement.TryGetProperty("channelEmotes", out var channel) && channel.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(ParseBttvArray(channel));
        }

        if (document.RootElement.TryGetProperty("sharedEmotes", out var shared) && shared.ValueKind == JsonValueKind.Array)
        {
            result.AddRange(ParseBttvArray(shared));
        }

        return result;
    }

    private async Task<IReadOnlyList<ThirdPartyEmote>> LoadSevenTvGlobalAsync(CancellationToken cancellationToken)
    {
        using var document = await TryGetJsonAsync(
            "https://7tv.io/v3/emote-sets/global",
            cancellationToken).ConfigureAwait(false);
        return document is { RootElement.ValueKind: JsonValueKind.Object } &&
               document.RootElement.TryGetProperty("emotes", out var emotes) &&
               emotes.ValueKind == JsonValueKind.Array
            ? ParseSevenTvArray(emotes)
            : [];
    }

    private async Task<IReadOnlyList<ThirdPartyEmote>> LoadSevenTvChannelAsync(
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        using var document = await TryGetJsonAsync(
            "https://7tv.io/v3/users/twitch/" + Uri.EscapeDataString(broadcasterId),
            cancellationToken).ConfigureAwait(false);
        return document is { RootElement.ValueKind: JsonValueKind.Object } &&
               document.RootElement.TryGetProperty("emote_set", out var set) &&
               set.ValueKind == JsonValueKind.Object &&
               set.TryGetProperty("emotes", out var emotes) &&
               emotes.ValueKind == JsonValueKind.Array
            ? ParseSevenTvArray(emotes)
            : [];
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("The third-party emote response is too large.");
        }

        using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("The third-party emote response is too large.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static List<ThirdPartyEmote> ParseBttvArray(JsonElement array)
    {
        var result = new List<ThirdPartyEmote>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("modifier", out var modifier) &&
                modifier.ValueKind == JsonValueKind.True)
            {
                // BTTV modifiers change the previous emote and are not regular
                // standalone images. Skip them until modifier compositing is used.
                continue;
            }
            var id = GetString(item, "id");
            var code = GetString(item, "code");
            if (id.Length > 0 && code.Length > 0)
            {
                result.Add(new ThirdPartyEmote(
                    id,
                    code,
                    new Uri($"https://cdn.betterttv.net/emote/{Uri.EscapeDataString(id)}/2x"),
                    ThirdPartyEmoteProviders.Bttv));
            }
        }

        return result;
    }

    internal static List<ThirdPartyEmote> ParseSevenTvArray(JsonElement array)
    {
        var result = new List<ThirdPartyEmote>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetString(item, "id");
            var code = GetString(item, "name");
            var flags = GetInt32(item, "flags");
            Uri? imageUri = null;
            var sourceWidth = 0;
            var sourceHeight = 0;
            if (item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var dataId = GetString(data, "id");
                if (dataId.Length > 0)
                {
                    id = dataId;
                }

                flags |= GetInt32(data, "flags");
                (imageUri, sourceWidth, sourceHeight) = TryGetSevenTvImage(data);
            }

            if (id.Length > 0 && code.Length > 0)
            {
                imageUri ??= new Uri($"https://cdn.7tv.app/emote/{Uri.EscapeDataString(id)}/2x.png");
                var names = new HashSet<string>(StringComparer.Ordinal) { code };
                if (item.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                {
                    foreach (var alias in aliases.EnumerateArray())
                    {
                        if (alias.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(alias.GetString()))
                        {
                            _ = names.Add(alias.GetString()!);
                        }
                    }
                }

                foreach (var name in names)
                {
                    result.Add(new ThirdPartyEmote(
                        id,
                        name,
                        imageUri,
                        ThirdPartyEmoteProviders.SevenTv,
                        IsZeroWidth: (flags & 256) != 0,
                        SourceWidth: sourceWidth,
                        SourceHeight: sourceHeight));
                }
            }
        }

        return result;
    }

    private static (Uri? Uri, int Width, int Height) TryGetSevenTvImage(JsonElement data)
    {
        if (!data.TryGetProperty("host", out var host) ||
            host.ValueKind != JsonValueKind.Object ||
            !host.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return (null, 0, 0);
        }

        var hostUrl = GetString(host, "url");
        if (hostUrl.StartsWith("//", StringComparison.Ordinal))
        {
            hostUrl = "https:" + hostUrl;
        }
        else if (hostUrl.StartsWith("/", StringComparison.Ordinal))
        {
            hostUrl = "https://cdn.7tv.app" + hostUrl;
        }
        if (!Uri.TryCreate(hostUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, 0, 0);
        }

        var descriptors = files.EnumerateArray()
            .Where(file => file.ValueKind == JsonValueKind.Object)
            .Select(file => new SevenTvFile(
                GetString(file, "name"),
                GetInt32(file, "width"),
                GetInt32(file, "height")))
            .Where(file => file.Name.Length > 0)
            .ToArray();
        var preferred = FindSevenTvFile(descriptors, "2x.gif", "1x.gif", "2x.webp", "1x.webp", "2x.png", "1x.png");
        return preferred is null
            ? (null, 0, 0)
            : (new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(preferred.Name)),
                preferred.Width,
                preferred.Height);
    }

    private static SevenTvFile? FindSevenTvFile(
        IReadOnlyList<SevenTvFile> files,
        params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            var match = files.FirstOrDefault(file =>
                string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private sealed record SourceLoadResult(
        string Provider,
        IReadOnlyList<ThirdPartyEmote> Emotes,
        bool Succeeded);

    private sealed record SevenTvFile(string Name, int Width, int Height);
}
