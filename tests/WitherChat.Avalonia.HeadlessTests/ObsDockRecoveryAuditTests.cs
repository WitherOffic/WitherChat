using System.IO.Pipes;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ObsDockRecoveryAuditTests
{
    [Fact]
    public async Task BusyPipeDoesNotBlockConstructionOrShutdown()
    {
        var pipe = "WitherChat-ObsRecovery-" + Guid.NewGuid().ToString("N");
        var blocker = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var constructor = Task.Run(() => new ObsDockIpcService(_ => Task.FromResult("OK 456"), pipe),
            TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAny(constructor, Task.Delay(750, TestContext.Current.CancellationToken));
            Assert.True(constructor.IsCompleted, "A busy IPC name blocked the application constructor.");
            var service = await constructor;
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
        finally
        {
            blocker.Dispose();
            var service = await constructor.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await service.DisposeAsync();
        }
    }

    [Fact]
    public async Task BusyPipeRecoversWhenPreviousOwnerLeaves()
    {
        var pipe = "WitherChat-ObsRecovery-" + Guid.NewGuid().ToString("N");
        var blocker = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var calls = 0;
        var constructor = Task.Run(() => new ObsDockIpcService(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("OK 456");
        }, pipe), TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAny(constructor, Task.Delay(750, TestContext.Current.CancellationToken));
            Assert.True(constructor.IsCompleted, "A busy IPC name blocked recovery.");
            blocker.Dispose();
            await using var service = await constructor;
            Assert.True(await ObsDockIpcService.RequestAsync(new(true, 123, 456), pipe, TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
        }
        finally
        {
            blocker.Dispose();
            var service = await constructor.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await service.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("en", "Light")]
    [InlineData("ru", "Dark")]
    public void DuplicateInstanceNotificationReadsTheSelectedProfile(string language, string theme)
    {
        var root = Path.Combine(Path.GetTempPath(), "WitherChat-ObsRecovery-" + Guid.NewGuid().ToString("N"));
        var paths = new WitherChat.Core.Services.AppDataPaths(Path.Combine(root, "profile"));
        try
        {
            paths.EnsureCreated();
            File.WriteAllText(paths.SettingsFile,
                System.Text.Json.JsonSerializer.Serialize(new { language, theme }));
            var notification = WitherChat.Desktop.Program.LoadSingleInstanceNotification(paths.ConfigDirectory);
            Assert.Equal(language, notification.Language);
            Assert.Equal(theme, notification.Theme);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BrokenClientCannotStopNextAttach()
    {
        var pipe = "WitherChat-ObsRecovery-" + Guid.NewGuid().ToString("N");
        await using var service = new ObsDockIpcService(_ => Task.FromResult("OK 456"), pipe);
        await using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(Encoding.ASCII.GetBytes("ATTACH 123"), timeout.Token);
        }
        Assert.True(await ObsDockIpcService.RequestAsync(new(true, 123, 456), pipe, TestContext.Current.CancellationToken));
    }
}

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task ObsDockWorkerShutdownDoesNotUpdateControlsFromWorkerThread()
    {
        await using var fixture = new WindowFixture();
        fixture.Window.ConfigureObsDockLayout(true);
        fixture.Window.Show();
        fixture.ViewModel.IsSettingsOpen = true;
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.False(fixture.Window.FindControl<Border>("ObsDockToolbar")!.IsEnabled);
        await Task.Run(async () => await fixture.ViewModel.DisposeAsync(), TestContext.Current.CancellationToken);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        fixture.Window.ConfigureObsDockLayout(false);
        Assert.False(fixture.ViewModel.IsObsDockMode);
    }
}
