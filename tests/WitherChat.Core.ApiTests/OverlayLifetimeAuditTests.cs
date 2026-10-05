using WitherChat.Core.Services;
using WitherChat.Core.Models;
using Xunit;
namespace WitherChat.Core.ApiTests;
public sealed class OverlayLifetimeAuditTests
{
    [Fact]
    public async Task RepeatedAndConcurrentDisposalIsSafe()
    {
        var server = new ObsOverlayServer();
        await server.DisposeAsync();
        await Task.WhenAll(server.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
        Assert.False(server.IsRunning);
    }
    [Fact]
    public async Task DisposedServerCannotBeReconfiguredOrRestarted()
    {
        var server = new ObsOverlayServer();
        await server.DisposeAsync();
        var options = new ObsOverlayOptions(17655, 12, 22, true, true, true, 0,
            true, true, true, 0, "left", "TornBlack");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.ConfigureAsync(false, options));
    }
}
