namespace WitherChat.Desktop.Services;

internal enum ObsPluginState { NotChecked, NotFound, SelectFolder, Missing, Installed, UpdateAvailable, Incompatible, Unavailable }
internal enum ObsPluginInstallCode { Success = 0, InvalidTarget = 20, ObsRunning = 21, Unavailable = 22, Failed = 23, RollbackFailed = 24, Busy = 25, Cancelled = 26, AccessDenied = 27, RunningFromPlugin = 28 }
internal sealed record ObsPluginInspection(ObsPluginState State, string Directory, bool CanInstall, bool ObsRunning = false, bool CanRemove = false);
internal sealed record ObsPluginInstallResult(ObsPluginInstallCode Code);
internal interface IObsPluginService
{
    Task<ObsPluginInspection> InspectAsync(string directory, CancellationToken cancellationToken);
    Task<ObsPluginInstallResult> InstallAsync(string directory);
    Task<ObsPluginInstallResult> RemoveAsync(string directory) => Task.FromResult(new ObsPluginInstallResult(ObsPluginInstallCode.Unavailable));
}
