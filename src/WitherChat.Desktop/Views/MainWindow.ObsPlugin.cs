using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Views;

public partial class MainWindow
{
    private async void BrowseObsPluginFolder_OnClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.CanManageObsPlugin) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = vm.Texts.ObsPluginChooseFolder,
                AllowMultiple = false
            });
            foreach (var folder in folders)
            {
                using (folder)
                {
                    if (folder.TryGetLocalPath() is { } path && vm.CanManageObsPlugin)
                    {
                        vm.ObsPluginDirectory = path;
                        await vm.RefreshObsPluginCommand.ExecuteAsync(null);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { AppDiagnostics.Write("OBS folder picker", exception); }
    }
}
