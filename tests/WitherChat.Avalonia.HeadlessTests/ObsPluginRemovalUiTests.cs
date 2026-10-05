using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(280, 340, "en", true)]
    [InlineData(280, 280, "ru", false)]
    [InlineData(360, 400, "ru", true)]
    [InlineData(1100, 760, "en", false)]
    public async Task ObsRemovalRequiresConfirmationAndFitsSmallSettings(int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var service = new RemovalUiService();
        vm.ObsPluginService = service;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        if (height == 280) fixture.Window.ConfigureObsDockLayout(true);
        fixture.Window.Show();
        await vm.ToggleSettingsCommand.ExecuteAsync(null);
        vm.SelectedSettingsSection = SettingsSection.Overlay;
        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
        vm.BeginRemoveObsPluginCommand.Execute(null);
        Assert.True(vm.IsObsPluginRemovalConfirmationOpen);
        Assert.Equal(0, service.RemoveCalls);
        var button = fixture.Window.FindControl<Button>("ConfirmRemoveObsPluginButton")!;
        await SettleAuxiliaryAuditAsync(fixture.Window);
        button.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        AssertAuxiliaryAuditInside(button, fixture.Window, width, height);
        using (var frame = fixture.Window.CaptureRenderedFrame())
            SaveAuditFrame(frame, $"obs-remove-confirm-{language}-{width}x{height}.png");
        vm.CancelRemoveObsPluginCommand.Execute(null);
        Assert.Equal(0, service.RemoveCalls);
        await vm.ConfirmRemoveObsPluginCommand.ExecuteAsync(null);
        Assert.Equal(0, service.RemoveCalls);
        vm.BeginRemoveObsPluginCommand.Execute(null);
        await vm.ConfirmRemoveObsPluginCommand.ExecuteAsync(null);
        Assert.Equal(1, service.RemoveCalls);
        Assert.False(vm.IsObsPluginRemovalConfirmationOpen);
        Assert.Equal(vm.Texts.ObsPluginMissing, vm.ObsPluginStatus);
        Assert.Equal(vm.Texts.ObsPluginRemoved, vm.ObsPluginNotice);
        Assert.False(vm.CanRemoveObsPlugin);
        Assert.True(vm.CanInstallObsPlugin);
    }

    [AvaloniaTheory]
    [InlineData((int)ObsPluginInstallCode.Failed)]
    [InlineData((int)ObsPluginInstallCode.Cancelled)]
    [InlineData((int)ObsPluginInstallCode.RunningFromPlugin)]
    public async Task ObsRemovalErrorsNeverClaimSuccess(int code)
    {
        await using var fixture = new WindowFixture();
        var service = new RemovalUiService { Result = (ObsPluginInstallCode)code };
        fixture.ViewModel.ObsPluginService = service;
        await fixture.ViewModel.RefreshObsPluginCommand.ExecuteAsync(null);
        fixture.ViewModel.BeginRemoveObsPluginCommand.Execute(null);
        await fixture.ViewModel.ConfirmRemoveObsPluginCommand.ExecuteAsync(null);
        Assert.Equal(fixture.ViewModel.Texts.ObsPluginInstalled, fixture.ViewModel.ObsPluginStatus);
        Assert.NotEqual(fixture.ViewModel.Texts.ObsPluginRemoved, fixture.ViewModel.ObsPluginNotice);
    }

    [AvaloniaFact]
    public async Task UpdatedTutorialsNeverInstallOrRemoveAndRestoreOriginalSettingsSection()
    {
        await using var fixture = new WindowFixture();
        var service = new RemovalUiService();
        var vm = fixture.ViewModel;
        vm.ObsPluginService = service;
        vm.StartOnboardingCommand.Execute(null);
        Assert.Equal(12, vm.OnboardingTotalSteps);
        while (vm.IsOnboardingOpen)
        {
            Assert.False(vm.CanInstallObsPlugin);
            Assert.False(vm.CanRemoveObsPlugin);
            Assert.NotEmpty(vm.OnboardingTitle);
            Assert.NotEmpty(vm.OnboardingDescription);
            vm.NextOnboardingCommand.Execute(null);
        }
        vm.IsSettingsOpen = true;
        vm.SelectedSettingsSection = SettingsSection.Account;
        vm.StartContextTutorialCommand.Execute("ObsPlugin");
        Assert.Equal(4, vm.OnboardingTotalSteps);
        Assert.Equal(SettingsSection.Overlay, vm.SelectedSettingsSection);
        while (vm.IsOnboardingOpen)
        {
            Assert.False(vm.CanConfirmRemoveObsPlugin);
            vm.NextOnboardingCommand.Execute(null);
        }
        Assert.Equal(SettingsSection.Account, vm.SelectedSettingsSection);
        Assert.Equal(0, service.RemoveCalls);
        Assert.Equal(0, service.InstallCalls);
    }

    [AvaloniaTheory]
    [InlineData(280, 340, "ru", false)]
    [InlineData(360, 400, "en", true)]
    [InlineData(1100, 760, "ru", false)]
    [InlineData(280, 280, "ru", false)]
    public async Task ObsPluginGuideNewStepsRenderAndRemainNavigable(int width, int height, string language, bool light)
    {
        await using var fixture = new WindowFixture();
        fixture.ViewModel.ObsPluginService = new RemovalUiService();
        ConfigureAuxiliaryAuditWindow(fixture, width, height, language, light);
        if (height == 280) fixture.Window.ConfigureObsDockLayout(true);
        fixture.Window.Show();
        fixture.ViewModel.IsSettingsOpen = true;
        fixture.ViewModel.StartContextTutorialCommand.Execute("ObsPlugin");
        for (var step = 0; step < 4; step++)
        {
            await Task.Delay(160);
            await SettleAuxiliaryAuditAsync(fixture.Window);
            var card = fixture.Window.FindControl<Border>("OnboardingCard")!;
            AssertAuxiliaryAuditInside(card, fixture.Window, width, height);
            Assert.False(fixture.ViewModel.CanRemoveObsPlugin);
            var scroll = fixture.Window.FindControl<ScrollViewer>("OnboardingContentScrollViewer")!;
            Assert.Equal(0, scroll.Offset.Y);
            if (height <= 340)
            {
                Assert.False(fixture.Window.FindControl<Grid>("OnboardingGuideHeader")!.IsVisible);
                var progress = fixture.Window.FindControl<TextBlock>("OnboardingShortProgress")!;
                Assert.True(progress.IsVisible);
                AssertAuxiliaryAuditInside(progress, fixture.Window, width, height);
            }
            AssertAuxiliaryAuditInside(fixture.Window.FindControl<Button>("OnboardingNextButton")!, fixture.Window, width, height);
            AssertAuxiliaryAuditInside(fixture.Window.FindControl<Button>("OnboardingSkipButton")!, fixture.Window, width, height);
            if (step > 0) AssertAuxiliaryAuditInside(fixture.Window.FindControl<Button>("OnboardingBackButton")!, fixture.Window, width, height);
            using var frame = fixture.Window.CaptureRenderedFrame();
            SaveAuditFrame(frame, $"obs-guide-{step}-{language}-{width}x{height}.png");
            fixture.ViewModel.NextOnboardingCommand.Execute(null);
        }
        Assert.False(fixture.ViewModel.IsOnboardingOpen);
    }

    [AvaloniaFact]
    public async Task ObsRemovalCancelSettingsDuringRemovalPreservesResultAndRestoresUnsavedOptions()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        var service = new RemovalUiService { RemovalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        vm.ObsPluginService = service;
        await vm.ToggleSettingsCommand.ExecuteAsync(null);
        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
        var previous = vm.AlwaysOnTop;
        vm.AlwaysOnTop = !previous;
        vm.BeginRemoveObsPluginCommand.Execute(null);
        var removal = vm.ConfirmRemoveObsPluginCommand.ExecuteAsync(null);
        Assert.True(vm.IsObsPluginBusy);
        Assert.False(vm.CanManageObsPlugin);
        await vm.CancelSettingsCommand.ExecuteAsync(null);
        service.RemovalGate.SetResult();
        await removal;
        Assert.Equal(previous, vm.AlwaysOnTop);
        Assert.Equal(@"D:\OBS Studio", vm.ObsPluginDirectory);
        Assert.Equal(vm.Texts.ObsPluginRemoved, vm.ObsPluginNotice);
        Assert.Equal(vm.Texts.ObsPluginMissing, vm.ObsPluginStatus);
        var store = (WitherChat.Core.Services.SettingsStore)typeof(MainWindowViewModel)
            .GetField("_settingsStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.Equal(previous, store.Load().AlwaysOnTop);
        Assert.Equal(@"D:\OBS Studio", store.Load().ObsPluginDirectory);
    }

    private sealed class RemovalUiService : IObsPluginService
    {
        public int RemoveCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public ObsPluginInstallCode Result { get; set; }
        public TaskCompletionSource? RemovalGate { get; set; }
        private bool _removed;
        public Task<ObsPluginInspection> InspectAsync(string directory, CancellationToken cancellationToken) =>
            Task.FromResult(new ObsPluginInspection(_removed ? ObsPluginState.Missing : ObsPluginState.Installed,
                @"D:\OBS Studio", _removed, false, !_removed));
        public Task<ObsPluginInstallResult> InstallAsync(string directory)
        { InstallCalls++; return Task.FromResult(new ObsPluginInstallResult(ObsPluginInstallCode.Success)); }
        public async Task<ObsPluginInstallResult> RemoveAsync(string directory)
        {
            RemoveCalls++;
            if (RemovalGate is not null) await RemovalGate.Task;
            _removed = Result == ObsPluginInstallCode.Success;
            return new ObsPluginInstallResult(Result);
        }
    }
}
