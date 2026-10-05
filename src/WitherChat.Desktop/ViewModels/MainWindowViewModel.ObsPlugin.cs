using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal IObsPluginService ObsPluginService { get; set; } = new WindowsObsPluginService();
    private ObsPluginInspection _obsPluginInspection = new(ObsPluginState.NotChecked, string.Empty, false);
    private ObsPluginInstallCode? _obsPluginNotice;
    [ObservableProperty] private string _obsPluginDirectory = string.Empty;
    [ObservableProperty] private bool _isObsPluginBusy;
    private bool _obsPluginInstalling;
    private bool _obsPluginRemoving;
    private bool _obsPluginRemovalNotice;
    [ObservableProperty] private bool _isObsPluginRemovalConfirmationOpen;

    public bool CanManageObsPlugin => !IsObsPluginBusy && !IsOnboardingOpen && !IsObsPluginRemovalConfirmationOpen;
    public bool CanInstallObsPlugin => CanManageObsPlugin && _obsPluginInspection.CanInstall;
    public bool CanRemoveObsPlugin => CanManageObsPlugin && _obsPluginInspection.CanRemove;
    public bool CanConfirmRemoveObsPlugin => !IsObsPluginBusy && !IsOnboardingOpen && IsObsPluginRemovalConfirmationOpen && _obsPluginInspection.CanRemove;
    public bool CanCancelRemoveObsPlugin => !IsObsPluginBusy && IsObsPluginRemovalConfirmationOpen;
    public string ObsPluginInstallLabel => _obsPluginInspection.State == ObsPluginState.UpdateAvailable ? Texts.ObsPluginUpdate : Texts.ObsPluginInstall;
    public string ObsPluginStatus => IsObsPluginBusy
        ? _obsPluginRemoving ? Texts.ObsPluginRemoving : _obsPluginInstalling ? Texts.ObsPluginInstalling : Texts.ObsPluginChecking
        : _obsPluginInspection.State switch
        {
            ObsPluginState.Installed => Texts.ObsPluginInstalled,
            ObsPluginState.Missing => Texts.ObsPluginMissing,
            ObsPluginState.UpdateAvailable => Texts.ObsPluginUpdateAvailable,
            ObsPluginState.NotFound => Texts.ObsPluginNotFound,
            ObsPluginState.SelectFolder => Texts.ObsPluginSelectFolder,
            ObsPluginState.Incompatible => Texts.ObsPluginIncompatible,
            ObsPluginState.Unavailable => Texts.ObsPluginUnavailable,
            _ => Texts.ObsPluginChecking
        };
    public string ObsPluginNotice => _obsPluginRemovalNotice && _obsPluginNotice is { } removalCode ? removalCode switch
    {
        ObsPluginInstallCode.Success => Texts.ObsPluginRemoved,
        ObsPluginInstallCode.Cancelled => Texts.ObsPluginRemovalCancelled,
        ObsPluginInstallCode.ObsRunning => Texts.ObsPluginCloseObs,
        ObsPluginInstallCode.RunningFromPlugin => Texts.ObsPluginRunningFromPlugin,
        ObsPluginInstallCode.Busy => Texts.ObsPluginBusy,
        _ => Texts.ObsPluginRemovalFailed
    } : _obsPluginNotice switch
    {
        ObsPluginInstallCode.Success => Texts.ObsPluginSuccess,
        ObsPluginInstallCode.Cancelled => Texts.ObsPluginCancelled,
        ObsPluginInstallCode.ObsRunning => Texts.ObsPluginCloseObs,
        ObsPluginInstallCode.RollbackFailed => Texts.ObsPluginRollbackFailed,
        ObsPluginInstallCode.Busy => Texts.ObsPluginBusy,
        ObsPluginInstallCode.Unavailable => Texts.ObsPluginUnavailable,
        ObsPluginInstallCode.InvalidTarget => Texts.ObsPluginIncompatible,
        ObsPluginInstallCode.Failed or ObsPluginInstallCode.AccessDenied => Texts.ObsPluginFailed,
        _ => _obsPluginInspection.ObsRunning ? Texts.ObsPluginCloseObs : Texts.ObsPluginHelp
    };

    private void RefreshObsPluginTextProperties()
    {
        OnPropertyChanged(nameof(ObsPluginStatus));
        OnPropertyChanged(nameof(ObsPluginNotice));
        OnPropertyChanged(nameof(ObsPluginInstallLabel));
        OnPropertyChanged(nameof(CanInstallObsPlugin));
        OnPropertyChanged(nameof(CanManageObsPlugin));
        InstallObsPluginCommand.NotifyCanExecuteChanged();
        RefreshObsPluginCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveObsPlugin));
        OnPropertyChanged(nameof(CanConfirmRemoveObsPlugin));
        OnPropertyChanged(nameof(CanCancelRemoveObsPlugin));
        BeginRemoveObsPluginCommand.NotifyCanExecuteChanged();
        ConfirmRemoveObsPluginCommand.NotifyCanExecuteChanged();
        CancelRemoveObsPluginCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsObsPluginBusyChanged(bool value) => RefreshObsPluginTextProperties();
    partial void OnIsObsPluginRemovalConfirmationOpenChanged(bool value) => RefreshObsPluginTextProperties();
    partial void OnIsOnboardingOpenChanged(bool value)
    {
        if (value) IsObsPluginRemovalConfirmationOpen = false;
        RefreshObsPluginTextProperties();
    }
    partial void OnObsPluginDirectoryChanged(string value)
    {
        _obsPluginInspection = new(ObsPluginState.NotChecked, value, false);
        _obsPluginNotice = null;
        _obsPluginRemovalNotice = false;
        IsObsPluginRemovalConfirmationOpen = false;
        RefreshObsPluginTextProperties();
        QueueSettingsSave();
    }
    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (value) _ = RefreshObsPluginAsync();
        else IsObsPluginRemovalConfirmationOpen = false;
    }
    partial void OnSelectedSettingsSectionChanged(SettingsSection value)
    {
        if (IsSettingsOpen && value == SettingsSection.Overlay) _ = RefreshObsPluginAsync();
    }

    [RelayCommand(CanExecute = nameof(CanManageObsPlugin))]
    private async Task RefreshObsPluginAsync()
    {
        if (_disposed || IsObsPluginBusy) return;
        IsObsPluginBusy = true;
        var directory = ObsPluginDirectory;
        try
        {
            var result = await ObsPluginService.InspectAsync(directory, _lifetimeCancellation.Token);
            if (_disposed || directory != ObsPluginDirectory) return;
            ObsPluginDirectory = result.Directory;
            _obsPluginInspection = result;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (!_disposed) _obsPluginInspection = new(ObsPluginState.Unavailable, directory, false);
        }
        finally
        {
            if (!_disposed) { IsObsPluginBusy = false; RefreshObsPluginTextProperties(); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallObsPlugin))]
    private async Task InstallObsPluginAsync()
    {
        if (_disposed || !CanInstallObsPlugin) return;
        var directory = ObsPluginDirectory;
        _obsPluginInstalling = true;
        _obsPluginRemovalNotice = false;
        _obsPluginNotice = null;
        IsObsPluginBusy = true;
        try
        {
            var result = await ObsPluginService.InstallAsync(directory);
            if (_disposed) return;
            _obsPluginNotice = result.Code;
            if (result.Code == ObsPluginInstallCode.Success)
            {
                // Preserve the installed folder even if settings were cancelled during the helper.
                ObsPluginDirectory = directory;
                _obsPluginNotice = result.Code;
                // Persist only the externally installed folder, not unsaved settings.
                if (_settingsOpenSnapshot is not null) _settingsOpenSnapshot.ObsPluginDirectory = directory;
                await SaveSettingsSafeAsync(_settingsOpenSnapshot ?? CreateSettingsSnapshot());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        { if (!_disposed) _obsPluginNotice = ObsPluginInstallCode.Failed; }
        finally
        {
            _obsPluginInstalling = false;
            if (!_disposed) { IsObsPluginBusy = false; RefreshObsPluginTextProperties(); }
        }
        if (!_disposed) await RefreshObsPluginAsync();
    }
}
