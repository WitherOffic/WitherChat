using System.IO.Pipes;
using System.Text;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ActivationRecoveryAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfinishedOrOversizeClientDoesNotBlockNextShow(bool oversize)
    {
        var pipe = "WitherChat-ActivationRecovery-" + Guid.NewGuid().ToString("N");
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new SingleInstanceActivationService(() => shown.TrySetResult(), pipe);
        await using var stalled = new NamedPipeClientStream(".", pipe, PipeDirection.Out,
            PipeOptions.Asynchronous | (OperatingSystem.IsWindows() ? PipeOptions.CurrentUserOnly : PipeOptions.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await stalled.ConnectAsync(timeout.Token);
        await stalled.WriteAsync(Encoding.ASCII.GetBytes(oversize ? new string('x', 129) : "SH"), timeout.Token);
        if (!oversize) await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
        Assert.True(await SingleInstanceActivationService.RequestShowWindowAsync(pipe, timeout.Token),
            "An unfinished or oversized client occupied the activation pipe.");
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
    }

    [Fact]
    public async Task CallbackFailureDoesNotKillNextShowRequest()
    {
        var pipe = "WitherChat-ActivationRecovery-" + Guid.NewGuid().ToString("N");
        var calls = 0;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new SingleInstanceActivationService(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                failed.TrySetResult();
                throw new InvalidOperationException("synthetic activation callback failure");
            }
            shown.TrySetResult();
        }, pipe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert.True(await SingleInstanceActivationService.RequestShowWindowAsync(pipe, timeout.Token));
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
        Assert.True(await SingleInstanceActivationService.RequestShowWindowAsync(pipe, timeout.Token));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(2), timeout.Token);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisposingServiceInterruptsUnfinishedClient()
    {
        var pipe = "WitherChat-ActivationRecovery-" + Guid.NewGuid().ToString("N");
        await using var service = new SingleInstanceActivationService(() => { }, pipe);
        await using var stalled = new NamedPipeClientStream(".", pipe, PipeDirection.Out,
            PipeOptions.Asynchronous | (OperatingSystem.IsWindows() ? PipeOptions.CurrentUserOnly : PipeOptions.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await stalled.ConnectAsync(timeout.Token);
        await stalled.WriteAsync(Encoding.ASCII.GetBytes("SH"), timeout.Token);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1), timeout.Token);
    }
}
