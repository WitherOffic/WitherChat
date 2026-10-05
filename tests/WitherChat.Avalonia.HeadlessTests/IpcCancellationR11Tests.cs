using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class IpcCancellationR11Tests
{
    [AvaloniaFact]
    public async Task TimeoutCancelsQueuedUiActionAndAllowsNextRequest()
    {
        var pipe = "WitherChat-IpcR11-" + Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        var calls = 0;
        await using var service = new ObsDockIpcService(async (_, token) =>
        {
            entered.Set();
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                calls++;
                return "OK 456";
            }, DispatcherPriority.Normal, token);
        }, pipe, TimeSpan.FromMilliseconds(150));
        var request = Task.Run(() => ObsDockIpcService.RequestAsync(new(true, 123, 456),
            pipe, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.True(SpinWait.SpinUntil(() => request.IsCompleted, 3000));
        Assert.False(await request);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.True(await ObsDockIpcService.RequestAsync(new(true, 123, 456),
            pipe, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task ShutdownCancelsQueuedUiActionWithoutAnotherUiTick()
    {
        var pipe = "WitherChat-IpcR11-" + Guid.NewGuid().ToString("N");
        using var entered = new ManualResetEventSlim();
        var calls = 0;
        var service = new ObsDockIpcService(async (_, token) =>
        {
            entered.Set();
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                calls++;
                return "OK 456";
            }, DispatcherPriority.Normal, token);
        }, pipe);
        var request = Task.Run(() => ObsDockIpcService.RequestAsync(new(true, 123, 456),
            pipe, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            var dispose = Task.Run(async () => await service.DisposeAsync(), TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(() => dispose.IsCompleted, 2000));
            await dispose;
            Assert.False(await request);
            await Task.Delay(30, TestContext.Current.CancellationToken);
            Assert.Equal(0, calls);
        }
        finally { await service.DisposeAsync(); }
    }

    [Fact]
    public async Task HandlerReceivesDeadlineAndListenerRecovers()
    {
        var pipe = "WitherChat-IpcR11-" + Guid.NewGuid().ToString("N");
        var calls = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new ObsDockIpcService(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return "OK 456";
        }, pipe, TimeSpan.FromMilliseconds(100));
        Assert.False(await ObsDockIpcService.RequestAsync(new(true, 123, 456),
            pipe, TestContext.Current.CancellationToken));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(await ObsDockIpcService.RequestAsync(new(true, 123, 456),
            pipe, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancelledDecodeDoesNotAllocateFrames()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ChatImageDecoder.Decode(
            new byte[] { 1, 2, 3 }, cancellation.Token));
    }
}
