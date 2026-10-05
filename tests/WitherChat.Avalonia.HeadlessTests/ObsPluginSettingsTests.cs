using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(280, 340, "en", true)]
    [InlineData(360, 400, "ru", true)]
    [InlineData(360, 400, "en", false)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(1100, 760, "en", true)]
    public async Task ObsPluginSettingsFitSmallDockAndInstallChangesStatus(int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var service = new FakeObsPluginService();
        vm.ObsPluginService = service;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        fixture.Window.Show();
        vm.IsSettingsOpen = true;
        vm.SelectedSettingsSection = SettingsSection.Overlay;
        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.Equal(vm.Texts.ObsPluginMissing, vm.ObsPluginStatus);
        var button = fixture.Window.FindControl<Button>("InstallObsPluginButton")!;
        Assert.True(button.IsEnabled);
        button.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        AssertAuxiliaryAuditInside(button, fixture.Window, width, height);
        using (var frame = fixture.Window.CaptureRenderedFrame())
            SaveAuditFrame(frame, $"obs-install-missing-{language}-{width}x{height}.png");
        await vm.InstallObsPluginCommand.ExecuteAsync(null);
        Assert.Equal(1, service.InstallCount);
        Assert.Equal(vm.Texts.ObsPluginInstalled, vm.ObsPluginStatus);
        Assert.Equal(vm.Texts.ObsPluginSuccess, vm.ObsPluginNotice);
        Assert.False(vm.CanInstallObsPlugin);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        using var installed = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(installed, $"obs-install-done-{language}-{width}x{height}.png");
    }

    [AvaloniaTheory]
    [InlineData((int)ObsPluginInstallCode.Cancelled)]
    [InlineData((int)ObsPluginInstallCode.Failed)]
    [InlineData((int)ObsPluginInstallCode.ObsRunning)]
    public async Task ObsPluginFailedOrCancelledInstallNeverShowsInstalled(int resultCode)
    {
        await using var fixture = new WindowFixture();
        var service = new FakeObsPluginService { Result = (ObsPluginInstallCode)resultCode };
        fixture.ViewModel.ObsPluginService = service;
        await fixture.ViewModel.RefreshObsPluginCommand.ExecuteAsync(null);
        await fixture.ViewModel.InstallObsPluginCommand.ExecuteAsync(null);
        Assert.Equal(fixture.ViewModel.Texts.ObsPluginMissing, fixture.ViewModel.ObsPluginStatus);
        Assert.NotEqual(fixture.ViewModel.Texts.ObsPluginSuccess, fixture.ViewModel.ObsPluginNotice);
    }

    [AvaloniaFact]
    public async Task ObsPluginInstalledFolderSurvivesSettingsCancelButOtherUnsavedOptionsDoNot()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.ObsPluginService = new FakeObsPluginService();
        await vm.ToggleSettingsCommand.ExecuteAsync(null);
        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
        var previous = vm.AlwaysOnTop;
        vm.AlwaysOnTop = !previous;
        await vm.InstallObsPluginCommand.ExecuteAsync(null);
        await vm.CancelSettingsCommand.ExecuteAsync(null);
        Assert.Equal(previous, vm.AlwaysOnTop);
        Assert.Equal(@"D:\OBS Studio", vm.ObsPluginDirectory);
        var store = (WitherChat.Core.Services.SettingsStore)typeof(MainWindowViewModel)
            .GetField("_settingsStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.Equal(@"D:\OBS Studio", store.Load().ObsPluginDirectory);
        Assert.Equal(previous, store.Load().AlwaysOnTop);
    }

    [AvaloniaTheory]
    [InlineData(280, 280)]
    [InlineData(360, 320)]
    public async Task ObsPluginSettingsRemainUsableAtMinimumObsDockSize(int width, int height)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        vm.ObsPluginService = new FakeObsPluginService();
        fixture.Window.ConfigureObsDockLayout(true);
        fixture.Window.Width = width;
        fixture.Window.Height = height;
        fixture.Window.Show();
        vm.IsSettingsOpen = true;
        vm.SelectedSettingsSection = SettingsSection.Overlay;
        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var button = fixture.Window.FindControl<Button>("InstallObsPluginButton")!;
        button.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        AssertAuxiliaryAuditInside(button, fixture.Window, width, height);
        Assert.True(button.IsEnabled);
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame, $"obs-plugin-settings-dock-{width}x{height}.png");
    }

    [AvaloniaFact]
    public async Task ObsPluginSettingsCancelDuringInstallKeepsInstalledFolderAndRestoresOtherSettings()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var service = new FakeObsPluginService { InstallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        vm.ObsPluginService = service;
        await vm.ToggleSettingsCommand.ExecuteAsync(null);
        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
        var previous = vm.AlwaysOnTop;
        vm.AlwaysOnTop = !previous;
        var installation = vm.InstallObsPluginCommand.ExecuteAsync(null);
        Assert.True(vm.IsObsPluginBusy);
        Assert.False(vm.CanManageObsPlugin);
        await vm.CancelSettingsCommand.ExecuteAsync(null);
        service.InstallGate.SetResult();
        await installation;
        Assert.Equal(previous, vm.AlwaysOnTop);
        Assert.Equal(@"D:\OBS Studio", vm.ObsPluginDirectory);
        Assert.Equal(vm.Texts.ObsPluginSuccess, vm.ObsPluginNotice);
    }

    private sealed class FakeObsPluginService : IObsPluginService
    {
        public ObsPluginInstallCode Result { get; set; }
        public int InstallCount { get; private set; }
        public TaskCompletionSource? InstallGate { get; set; }
        private bool _installed;
        public Task<ObsPluginInspection> InspectAsync(string directory, CancellationToken cancellationToken) =>
            Task.FromResult(new ObsPluginInspection(_installed ? ObsPluginState.Installed : ObsPluginState.Missing, @"D:\OBS Studio", !_installed));
        public async Task<ObsPluginInstallResult> InstallAsync(string directory)
        {
            InstallCount++;
            if (InstallGate is not null) await InstallGate.Task;
            _installed = Result == ObsPluginInstallCode.Success;
            return new ObsPluginInstallResult(Result);
        }
    }
}
