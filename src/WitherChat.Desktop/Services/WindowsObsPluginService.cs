using System.ComponentModel;
using System.Diagnostics;

namespace WitherChat.Desktop.Services;

internal sealed class WindowsObsPluginService : IObsPluginService
{
    public Task<ObsPluginInspection> InspectAsync(string directory, CancellationToken cancellationToken) =>
        Task.Run(() => Inspect(directory, cancellationToken), cancellationToken);

    private static ObsPluginInspection Inspect(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WindowsObsInstallation.IsSupported) return new(ObsPluginState.Unavailable, directory, false);
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                var candidates = WindowsObsInstallation.FindCandidates();
                if (candidates.Count != 1)
                    return new(candidates.Count == 0 ? ObsPluginState.NotFound : ObsPluginState.SelectFolder, string.Empty, false);
                directory = candidates[0];
            }
            var probe = new WindowsObsInstallation();
            var root = WindowsObsInstallation.Validate(directory, probe, requireCompatible: false);
            var running = probe.ObsRunning();
            var canRemove = !running && ObsPluginPayload.CurrentSingleFileExe() is not null && ObsPluginRemover.FindFiles(root).Count > 0;
            try { WindowsObsInstallation.Validate(root, probe); }
            catch (InvalidDataException) { return new(ObsPluginState.Incompatible, root, false, running, canRemove); }
            var payload = ObsPluginPayload.LoadEmbedded();
            var source = ObsPluginPayload.CurrentSingleFileExe();
            if (payload is null || source is null) return new(ObsPluginState.Unavailable, root, false, running, canRemove);
            var installedExe = WindowsObsInstallation.OwnedPath(root, ObsPluginPayload.ExePath);
            var dll = WindowsObsInstallation.OwnedPath(root, ObsPluginPayload.DllPath);
            var missing = !File.Exists(dll) && !File.Exists(installedExe);
            var matches = File.Exists(installedExe) && ObsPluginPayload.Hash(installedExe) == ObsPluginPayload.Hash(source);
            foreach (var item in payload.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                matches &= payload.Matches(item.Key, WindowsObsInstallation.OwnedPath(root, item.Key));
            }
            var state = missing ? ObsPluginState.Missing : matches ? ObsPluginState.Installed : ObsPluginState.UpdateAvailable;
            return new(state, root, !running && state != ObsPluginState.Installed, running, canRemove);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return new(ObsPluginState.Incompatible, directory, false); }
    }

    public async Task<ObsPluginInstallResult> InstallAsync(string directory)
    {
        if (!WindowsObsInstallation.IsSupported || ObsPluginPayload.CurrentSingleFileExe() is not { } exe)
            return new(ObsPluginInstallCode.Unavailable);
        try
        {
            var root = WindowsObsInstallation.Validate(directory, new WindowsObsInstallation());
            if (new WindowsObsInstallation().ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
            // An independent helper can complete/roll back even if the chat window closes.
            var result = await RunHelperAsync(exe, root, elevate: false).ConfigureAwait(false);
            if (result.Code != ObsPluginInstallCode.AccessDenied) return result;
            // Retry a permission failure only after all previous writes were safely rolled back.
            return await RunHelperAsync(exe, root, elevate: true).ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return new(ObsPluginInstallCode.Cancelled); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or InvalidOperationException)
        { return new(ObsPluginInstallCode.Failed); }
    }

    public async Task<ObsPluginInstallResult> RemoveAsync(string directory)
    {
        if (!WindowsObsInstallation.IsSupported || ObsPluginPayload.CurrentSingleFileExe() is not { } exe)
            return new(ObsPluginInstallCode.Unavailable);
        try
        {
            var root = WindowsObsInstallation.Validate(directory, new WindowsObsInstallation(), requireCompatible: false);
            if (new WindowsObsInstallation().ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
            if (string.Equals(exe, WindowsObsInstallation.OwnedPath(root, ObsPluginPayload.ExePath), StringComparison.OrdinalIgnoreCase))
                return new(ObsPluginInstallCode.RunningFromPlugin);
            var result = await RunHelperAsync(exe, root, elevate: false, remove: true).ConfigureAwait(false);
            return result.Code == ObsPluginInstallCode.AccessDenied
                ? await RunHelperAsync(exe, root, elevate: true, remove: true).ConfigureAwait(false) : result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        { return new(ObsPluginInstallCode.Cancelled); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception or InvalidOperationException)
        { return new(ObsPluginInstallCode.Failed); }
    }

    internal static ObsPluginInstallResult RunRemover(string directory)
    {
        try
        {
            if (!WindowsObsInstallation.IsSupported || ObsPluginPayload.CurrentSingleFileExe() is not { } exe)
                return new(ObsPluginInstallCode.Unavailable);
            return new ObsPluginRemover(new WindowsObsInstallation(), exe).Remove(directory);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        { return new(ObsPluginInstallCode.Failed); }
    }

    private static async Task<ObsPluginInstallResult> RunHelperAsync(string exe, string root, bool elevate, bool remove = false)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (elevate) start.Verb = "runas";
        start.ArgumentList.Add(remove ? "--remove-obs-plugin" : "--install-obs-plugin");
        start.ArgumentList.Add(root);
        using var process = Process.Start(start);
        if (process is null) return new(ObsPluginInstallCode.Failed);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new(Enum.IsDefined(typeof(ObsPluginInstallCode), process.ExitCode)
            ? (ObsPluginInstallCode)process.ExitCode : ObsPluginInstallCode.Failed);
    }

    internal static ObsPluginInstallResult RunInstaller(string directory)
    {
        try
        {
            if (!WindowsObsInstallation.IsSupported || ObsPluginPayload.CurrentSingleFileExe() is not { } exe ||
                ObsPluginPayload.LoadEmbedded() is not { } payload) return new(ObsPluginInstallCode.Unavailable);
            return new ObsPluginInstaller(payload, exe, new WindowsObsInstallation()).Install(directory);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(ObsPluginInstallCode.Failed); }
    }
}
