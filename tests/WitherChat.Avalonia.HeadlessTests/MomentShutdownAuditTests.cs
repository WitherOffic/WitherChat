using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsQueuedMomentChangesWithoutLosingPreviousData(bool deleting)
    {
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment]);
        await using var fixture = new WindowFixture(streamMomentStore: store);
        fixture.Window.Show();
        var vm = fixture.ViewModel;
        vm.BeginSaveMomentCommand.Execute(MomentAuditMessage(fixture));
        vm.MomentNote = "pending";
        var gate = (SemaphoreSlim)typeof(StreamMomentStore)
            .GetField("_saveGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        await gate.WaitAsync();
        var change = deleting
            ? vm.DeleteMomentCommand.ExecuteAsync(Assert.Single(vm.Moments))
            : vm.ConfirmSaveMomentCommand.ExecuteAsync(null);
        try
        {
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await change.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsMomentsSaving);
            Assert.False(vm.BeginSaveMomentCommand.CanExecute(MomentAuditMessage(fixture)));
            Assert.False(vm.ConfirmSaveMomentCommand.CanExecute(null));
            Assert.False(vm.DeleteMomentCommand.CanExecute(Assert.Single(vm.Moments)));
            using var reader = new StreamMomentStore(files.Paths);
            Assert.Equal(files.Moment, Assert.Single(reader.Load()));
            Assert.False(File.Exists(files.Paths.MomentsFile + ".tmp"));
        }
        finally
        {
            try { gate.Release(); }
            catch (ObjectDisposedException) { } // The cancelled save already drained during shutdown.
        }
    }

    [AvaloniaFact]
    public async Task MomentReadRetryKeepsLatestSavedMomentFirst()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        var latest = files.Moment with { Id = "latest-save",
            MessageTimestamp = files.Moment.MessageTimestamp.AddDays(-1),
            SavedAtUtc = files.Moment.SavedAtUtc.AddDays(1) };
        await store.SaveAsync([latest, files.Moment]);
        using var locked = new FileStream(files.Paths.MomentsFile, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        await using var fixture = new WindowFixture(streamMomentStore: store);
        Assert.Empty(fixture.ViewModel.Moments);
        locked.Dispose();
        ConfigureAuxiliaryAuditWindow(fixture, 280, 340, "ru", false);
        fixture.Window.Show();
        fixture.ViewModel.OpenMomentsPanelCommand.Execute(null);
        Assert.Equal(new[] { latest.Id, files.Moment.Id },
            fixture.ViewModel.GetMomentSnapshot().Select(value => value.Id));
        await SettleAuxiliaryAuditAsync(fixture.Window);
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, "moment-order-retry-ru-dark-280x340.png");
    }
}
