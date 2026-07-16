using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class BttvEmoteProvider : IThirdPartyEmoteProvider, IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _http = new(new HttpClientHandler { CheckCertificateRevocationList = true })
    {
        BaseAddress = new Uri("https://api.betterttv.net/3/"),
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly FileLogger _logger;

    public BttvEmoteProvider(FileLogger logger)
    {
        _logger = logger;
    }

    public string Name => "BTTV";

    public void Dispose() => _http.Dispose();

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
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn($"BTTV emotes failed: {(int)response.StatusCode} {url}");
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
            _logger.Warn($"BTTV provider disabled for this refresh: {ex.GetType().Name}");
            return null;
        }
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("BTTV response exceeds the size limit.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("BTTV response exceeds the size limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        output.Position = 0;
        return await JsonDocument.ParseAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false);
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
