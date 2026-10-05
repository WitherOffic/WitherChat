using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class SessionIsolationR11Tests
{
    [Theory]
    [InlineData(0, "channel-s0")]
    [InlineData(1, "channel-s1")]
    [InlineData(int.MaxValue, "channel-s2147483647")]
    public void SessionSuffixMatchesNativeProtocol(int session, string expected) =>
        Assert.Equal(expected, SessionPipeName.ForSession("channel", session));

    [Fact]
    public void InvalidSessionCannotCreateAChannel() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionPipeName.ForSession("channel", -1));

    [Fact]
    public async Task DifferentSessionNamesRouteRepeatedObsRequestsToTheirOwnHandler()
    {
        var root = "WitherChat-SessionR11-" + Guid.NewGuid().ToString("N");
        var firstName = SessionPipeName.ForSession(root, 1);
        var secondName = SessionPipeName.ForSession(root, 2);
        var firstCalls = 0;
        var secondCalls = 0;
        await using var first = new ObsDockIpcService(request =>
        {
            Interlocked.Increment(ref firstCalls);
            return Task.FromResult(request.ProcessId == 111 ? "OK 456" : "ERROR wrong-session");
        }, firstName);
        await using var second = new ObsDockIpcService(request =>
        {
            Interlocked.Increment(ref secondCalls);
            return Task.FromResult(request.ProcessId == 222 ? "OK 456" : "ERROR wrong-session");
        }, secondName);
        for (var i = 0; i < 40; i++)
        {
            var results = await Task.WhenAll(
                ObsDockIpcService.RequestAsync(new(true, 111, 456), firstName, TestContext.Current.CancellationToken),
                ObsDockIpcService.RequestAsync(new(true, 222, 456), secondName, TestContext.Current.CancellationToken));
            Assert.All(results, result => Assert.True(result));
        }
        Assert.Equal(40, firstCalls);
        Assert.Equal(40, secondCalls);
    }
}
