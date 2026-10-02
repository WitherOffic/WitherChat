using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340)]
    [InlineData(1100, 760)]
    public async Task FailedMomentSaveKeepsDraftVisibleAndCanBeRetried(int width, int height)
    {
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        await using var fixture = new WindowFixture(streamMomentStore: store);
        ConfigureAuxiliaryAuditWindow(fixture, width, height, "ru", false);
        var window = fixture.Window;
        var vm = fixture.ViewModel;
        window.Show();
        var message = MomentAuditMessage(fixture);
        vm.BeginSaveMomentCommand.Execute(message);
        vm.MomentNote = "Важная заметка 👋";
        Directory.CreateDirectory(files.Paths.MomentsFile + ".tmp");
        await vm.ConfirmSaveMomentCommand.ExecuteAsync(null);
        Assert.True(vm.IsMomentEditorOpen);
        Assert.Same(message, vm.MomentTarget);
        Assert.Equal("Важная заметка 👋", vm.MomentNote);
        Assert.Empty(vm.Moments);
        Assert.NotEmpty(vm.StatusDetail);
        await SettleAuxiliaryAuditAsync(window);
        var error = window.FindControl<Border>("MomentEditorCard")!.GetLogicalDescendants()
            .OfType<TextBlock>().Single(text => text.Text == vm.MomentsStatus);
        error.BringIntoView();
        await SettleAuxiliaryAuditAsync(window);
        AssertAuxiliaryAuditInside(error, window, width, height);
        using var frame = window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"moment-save-error-ru-dark-{width}x{height}.png");
        Directory.Delete(files.Paths.MomentsFile + ".tmp");
        await vm.ConfirmSaveMomentCommand.ExecuteAsync(null);
        Assert.False(vm.IsMomentEditorOpen);
        Assert.Equal("Важная заметка 👋", Assert.Single(store.Load()).Note);
        Assert.Single(vm.Moments);
    }

    [AvaloniaTheory]
    [InlineData(280, 340)]
    [InlineData(1100, 760)]
    public async Task FailedMomentDeletionKeepsItemAndCanBeRetried(int width, int height)
    {
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment]);
        await using var fixture = new WindowFixture(streamMomentStore: store);
        ConfigureAuxiliaryAuditWindow(fixture, width, height, "ru", false);
        fixture.Window.Show();
        fixture.ViewModel.OpenMomentsPanelCommand.Execute(null);
        var item = Assert.Single(fixture.ViewModel.Moments);
        Directory.CreateDirectory(files.Paths.MomentsFile + ".tmp");
        await fixture.ViewModel.DeleteMomentCommand.ExecuteAsync(item);
        Assert.Same(item, Assert.Single(fixture.ViewModel.Moments));
        Assert.Equal(files.Moment, Assert.Single(store.Load()));
        Directory.Delete(files.Paths.MomentsFile + ".tmp");
        await fixture.ViewModel.DeleteMomentCommand.ExecuteAsync(item);
        Assert.Empty(fixture.ViewModel.Moments);
        Assert.Empty(store.Load());
    }

    [AvaloniaFact]
    public async Task PendingSaveDisablesConflictingMomentMutations()
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
        var save = vm.ConfirmSaveMomentCommand.ExecuteAsync(null);
        try
        {
            Assert.False(vm.DeleteMomentCommand.CanExecute(Assert.Single(vm.Moments)));
            Assert.False(vm.CancelSaveMomentCommand.CanExecute(null));
            Assert.False(vm.BeginSaveMomentCommand.CanExecute(MomentAuditMessage(fixture)));
        }
        finally
        {
            gate.Release();
            await save;
        }
        Assert.Equal(2, vm.Moments.Count);
        Assert.Equal(2, store.Load().Count);
    }
    [AvaloniaFact]
    public async Task ReopeningMomentsRecoversAfterTemporaryReadFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var files = new MomentAuditFiles();
        using var store = new StreamMomentStore(files.Paths);
        await store.SaveAsync([files.Moment]);
        var locked = new FileStream(files.Paths.MomentsFile, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            await using var fixture = new WindowFixture(streamMomentStore: store);
            Assert.Empty(fixture.ViewModel.Moments);
            locked.Dispose();
            fixture.Window.Show();
            fixture.ViewModel.OpenMomentsPanelCommand.Execute(null);
            Assert.Equal(files.Moment, Assert.Single(fixture.ViewModel.Moments).Value);
            Assert.False(store.HadLoadFailure);
            Assert.Empty(fixture.ViewModel.MomentsStatus);
        }
        finally { locked.Dispose(); }
    }

    private static ChatMessageItemViewModel MomentAuditMessage(WindowFixture fixture) =>
        new(new ChatMessage { Id = "new", Channel = "audit", UserLogin = "viewer",
            DisplayName = "Viewer", Text = "Привет 👋", Timestamp = DateTimeOffset.UtcNow },
            fixture.ImageCache, fixture.ViewModel.Texts);

    private sealed class MomentAuditFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "WitherChat-ui-moment-audit-" + Guid.NewGuid().ToString("N"));
        public AppDataPaths Paths { get; }
        public StreamMoment Moment { get; } = new("first", ChatPlatforms.Twitch,
            "audit", "message", "Viewer", "Привет 👋", "note",
            DateTimeOffset.Parse("2026-10-03T01:00:00Z"), DateTimeOffset.Parse("2026-10-03T01:01:00Z"));
        public MomentAuditFiles()
        {
            Paths = new AppDataPaths(DirectoryPath);
            Paths.EnsureCreated();
        }
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
