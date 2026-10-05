using CommunityToolkit.Mvvm.Input;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanRemoveObsPlugin))]
    private void BeginRemoveObsPlugin()
    {
        if (!_disposed && CanRemoveObsPlugin) IsObsPluginRemovalConfirmationOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanCancelRemoveObsPlugin))]
    private void CancelRemoveObsPlugin() => IsObsPluginRemovalConfirmationOpen = false;

    [RelayCommand(CanExecute = nameof(CanConfirmRemoveObsPlugin))]
    private async Task ConfirmRemoveObsPluginAsync()
    {
        if (_disposed || !CanConfirmRemoveObsPlugin) return;
        var directory = ObsPluginDirectory;
        _obsPluginRemoving = true;
        _obsPluginRemovalNotice = true;
        _obsPluginNotice = null;
        IsObsPluginBusy = true;
        try
        {
            var result = await ObsPluginService.RemoveAsync(directory);
            if (!_disposed)
            {
                if (result.Code == ObsPluginInstallCode.Success)
                {
                    ObsPluginDirectory = directory;
                    if (_settingsOpenSnapshot is not null) _settingsOpenSnapshot.ObsPluginDirectory = directory;
                    await SaveSettingsSafeAsync(_settingsOpenSnapshot ?? CreateSettingsSnapshot());
                }
                _obsPluginRemovalNotice = true;
                _obsPluginNotice = result.Code;
                IsObsPluginRemovalConfirmationOpen = false;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (!_disposed)
            {
                _obsPluginNotice = ObsPluginInstallCode.Failed;
                IsObsPluginRemovalConfirmationOpen = false;
            }
        }
        finally
        {
            _obsPluginRemoving = false;
            if (!_disposed) { IsObsPluginBusy = false; RefreshObsPluginTextProperties(); }
        }
        if (!_disposed) await RefreshObsPluginAsync();
    }
}
