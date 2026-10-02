using System.Net;
using System.Net.WebSockets;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class EventSubReconnectAuditTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransientTimeoutOrSocketFailureReconnectsWithDefaultEndpoint(bool timeout)
    {
        var endpoint = new Uri("wss://eventsub.example.test/ws");
        using var cancellation = new CancellationTokenSource();
        var attempts = new List<(Uri Endpoint, bool Subscribe)>();
        await TwitchEventSubClient.RunReconnectLoopAsync((uri, subscribe, token) =>
        {
            attempts.Add((uri, subscribe));
            if (attempts.Count == 1)
                return Task.FromException<Uri?>(timeout
                    ? new TaskCanceledException("HTTP request timed out")
                    : new WebSocketException("Temporary disconnect"));
            cancellation.Cancel();
            return Task.FromException<Uri?>(new OperationCanceledException(token));
        }, endpoint, TimeSpan.Zero, cancellation.Token);
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt =>
        {
            Assert.Equal(endpoint, attempt.Endpoint);
            Assert.True(attempt.Subscribe);
        });
    }

    [Fact]
    public async Task TimeoutOnReconnectUrlRestoresDefaultEndpointAndSubscriptions()
    {
        var endpoint = new Uri("wss://eventsub.example.test/ws");
        var reconnect = new Uri("wss://eventsub.example.test/ws?reconnect=session");
        using var cancellation = new CancellationTokenSource();
        var attempts = new List<(Uri Endpoint, bool Subscribe)>();
        await TwitchEventSubClient.RunReconnectLoopAsync((uri, subscribe, token) =>
        {
            attempts.Add((uri, subscribe));
            if (attempts.Count == 1) return Task.FromResult<Uri?>(reconnect);
            if (attempts.Count == 2) return Task.FromException<Uri?>(new TaskCanceledException("HTTP timeout"));
            cancellation.Cancel();
            return Task.FromException<Uri?>(new OperationCanceledException(token));
        }, endpoint, TimeSpan.Zero, cancellation.Token);
        Assert.Equal(new[] { endpoint, reconnect, endpoint }, attempts.Select(attempt => attempt.Endpoint));
        Assert.Equal(new[] { true, false, true }, attempts.Select(attempt => attempt.Subscribe));
    }

    [Fact]
    public async Task CallerCancellationStopsReconnectWithoutAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await TwitchEventSubClient.RunReconnectLoopAsync((uri, subscribe, token) =>
        {
            attempts++;
            cancellation.Cancel();
            return Task.FromException<Uri?>(new OperationCanceledException(token));
        }, new Uri("wss://eventsub.example.test/ws"), TimeSpan.Zero, cancellation.Token);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task UnexpectedProgrammingErrorIsNotSilentlyRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TwitchEventSubClient.RunReconnectLoopAsync((uri, subscribe, token) =>
            {
                attempts++;
                return Task.FromException<Uri?>(new InvalidOperationException("Unexpected state"));
            }, new Uri("wss://eventsub.example.test/ws"), TimeSpan.Zero, CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task InitialProfileTimeoutIsReportedAsNetworkFailureNotCallerCancellation()
    {
        using var api = new TwitchChatApiClient(handler: new TimeoutHandler());
        await using var eventSub = new TwitchEventSubClient(api);
        var session = new TwitchAuthSession("test-token", "", TwitchApplication.ClientId, "owner-id", "owner",
            TwitchApplication.RequiredScopes, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            eventSub.ConfigureAsync(session, ["channel"], TestContext.Current.CancellationToken));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("Simulated CDN timeout"));
    }
}
