using System.Reflection;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ActivationShutdownR11Tests
{
    [Fact]
    public async Task ConcurrentDisposalWaitsForActivationCleanup()
    {
        var service = new SingleInstanceActivationService(() => { },
            "WitherChat-ActivationR11-" + Guid.NewGuid().ToString("N"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var registration = Source(service).Token.Register(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        });
        var first = Task.Run(async () => await service.DisposeAsync(), TestContext.Current.CancellationToken);
        Task? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            second = Task.Run(async () => await service.DisposeAsync(), TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted, "Another disposer must await active activation-channel cleanup.");
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancellationFailureStillClosesActivationListener()
    {
        var service = new SingleInstanceActivationService(() => { },
            "WitherChat-ActivationR11-" + Guid.NewGuid().ToString("N"));
        using var registration = Source(service).Token.Register(() =>
            throw new InvalidOperationException("test callback"));
        var first = service.DisposeAsync().AsTask();
        var second = service.DisposeAsync().AsTask();
        var firstError = await Assert.ThrowsAsync<AggregateException>(() => first);
        var secondError = await Assert.ThrowsAsync<AggregateException>(() => second);
        Assert.Same(firstError, secondError);
        var listener = (Task)typeof(SingleInstanceActivationService).GetField("_listenerTask",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        Assert.True(listener.IsCompleted);
    }

    private static CancellationTokenSource Source(SingleInstanceActivationService service) =>
        (CancellationTokenSource)typeof(SingleInstanceActivationService).GetField("_cancellation",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
}
