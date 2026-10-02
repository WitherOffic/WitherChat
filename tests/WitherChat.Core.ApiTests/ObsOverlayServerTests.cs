using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class ObsOverlayServerTests
{
    [Fact]
    public async Task ClearChannelPurgesOnlyTargetHistoryAndNotifiesConnectedOverlay()
    {
        await using var overlay = new ObsOverlayServer();
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        await overlay.ConfigureAsync(
            true,
            new ObsOverlayOptions(
                port, 12, 22, true, true, true, 0, true, true, true, 0, "flex-start", "Default"));
        overlay.Publish(CreateMessage("target-old", "target"));
        overlay.Publish(CreateMessage("other-old", "other"));
        overlay.ClearChannel("#TARGET");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var chatResponse = await http.GetAsync(
            $"http://localhost:{port}/overlay/chat",
            TestContext.Current.CancellationToken);
        chatResponse.EnsureSuccessStatusCode();
        Assert.Contains(
            "default-src 'none'",
            chatResponse.Headers.GetValues("Content-Security-Policy").Single(),
            StringComparison.Ordinal);
        Assert.Equal("no-referrer", chatResponse.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("SAMEORIGIN", chatResponse.Headers.GetValues("X-Frame-Options").Single());
        var html = await chatResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("m.type==='clear'", html, StringComparison.Ordinal);
        Assert.Contains("row.dataset.channel", html, StringComparison.Ordinal);

        using var response = await http.GetAsync(
            $"http://localhost:{port}/overlay/events",
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken));

        using (var history = JsonDocument.Parse(await ReadEventDataAsync(reader)))
        {
            Assert.Equal("other-old", history.RootElement.GetProperty("id").GetString());
            Assert.Equal("other", history.RootElement.GetProperty("channel").GetString());
        }

        overlay.ClearChannel("other");
        using var clear = JsonDocument.Parse(await ReadEventDataAsync(reader));
        Assert.Equal("clear", clear.RootElement.GetProperty("type").GetString());
        Assert.Equal("other", clear.RootElement.GetProperty("channel").GetString());
    }

    private static ChatMessage CreateMessage(string id, string channel) => new()
    {
        Id = id,
        Channel = channel,
        UserLogin = "viewer",
        DisplayName = "Viewer",
        Text = id,
        Timestamp = DateTimeOffset.UtcNow,
        Parts = [ChatMessagePart.PlainText(id)]
    };

    private static async Task<string> ReadEventDataAsync(StreamReader reader)
    {
        for (var index = 0; index < 20; index++)
        {
            var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (line?.StartsWith("data: ", StringComparison.Ordinal) == true)
            {
                return line[6..];
            }
        }

        throw new InvalidDataException("The OBS overlay did not return an event payload.");
    }
}
