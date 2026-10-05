using System.Reflection;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ObsDockShutdownAuditTests
{
    [Fact]
    public async Task ConcurrentDisposalWaitsForTheFirstCleanup()
    {
        var service = new ObsDockIpcService(_ => Task.FromResult("OK 456"),
            "WitherChat-ObsShutdown-" + Guid.NewGuid().ToString("N"));
        var shutdown = GetShutdownSource(service);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var callback = shutdown.Token.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        });
        var first = Task.Run(async () => await service.DisposeAsync(),
            TestContext.Current.CancellationToken);
        Task? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            second = Task.Run(async () => await service.DisposeAsync(),
                TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted, "A concurrent DisposeAsync returned before the active cleanup completed.");
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (second is not null)
                await second.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatedDisposalKeepsTheSameSuccessfulCompletion()
    {
        var service = new ObsDockIpcService(_ => Task.FromResult("OK 456"),
            "WitherChat-ObsShutdown-" + Guid.NewGuid().ToString("N"));
        await service.DisposeAsync();
        await service.DisposeAsync();
        await service.DisposeAsync();
        Assert.True(GetShutdownSource(service).IsCancellationRequested);
    }

    [Fact]
    public async Task CancellationFailureStillClosesListenerAndReachesEveryDisposer()
    {
        var service = new ObsDockIpcService(_ => Task.FromResult("OK 456"),
            "WitherChat-ObsShutdown-" + Guid.NewGuid().ToString("N"));
        using var callback = GetShutdownSource(service).Token.Register(() =>
            throw new InvalidOperationException("test cancellation callback"));
        var first = service.DisposeAsync().AsTask();
        var second = service.DisposeAsync().AsTask();
        var firstError = await Assert.ThrowsAsync<AggregateException>(() => first);
        var secondError = await Assert.ThrowsAsync<AggregateException>(() => second);
        Assert.Same(firstError, secondError);
        var listener = (Task)typeof(ObsDockIpcService).GetField("_listener",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        Assert.True(listener.IsCompleted);
    }

    private static CancellationTokenSource GetShutdownSource(ObsDockIpcService service) =>
        (CancellationTokenSource)typeof(ObsDockIpcService).GetField("_shutdown",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
}
