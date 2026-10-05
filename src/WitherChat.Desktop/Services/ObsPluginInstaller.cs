using System.Security.Cryptography;
using System.Text;

namespace WitherChat.Desktop.Services;

internal sealed class ObsPluginInstaller(ObsPluginPayload payload, string sourceExe, IObsInstallationProbe probe)
{
    internal ObsPluginInstallResult Install(string directory, Action<int>? beforeWrite = null)
    {
        string root;
        try { root = WindowsObsInstallation.Validate(directory, probe); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        { return new(ObsPluginInstallCode.InvalidTarget); }
        if (probe.ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
        if (!File.Exists(sourceExe)) return new(ObsPluginInstallCode.Unavailable);
        var name = @"Global\WitherChat.ObsInstaller." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        using var mutex = new Mutex(false, name);
        var acquired = false;
        var changed = new List<(string Target, string? Backup)>();
        var temporaryFiles = new List<string>();
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(ObsPluginInstallCode.Busy);
            var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var staging = WindowsObsInstallation.OwnedPath(root, "data/obs-plugins/witherchat-obs-installer/" + id);
            var backup = WindowsObsInstallation.OwnedPath(root, "data/obs-plugins/witherchat-obs-backups/" + id);
            Directory.CreateDirectory(staging);
            var targets = new List<(string Relative, string Stage)>();
            foreach (var item in payload.Files)
            {
                var stage = WindowsObsInstallation.OwnedPath(root, "data/obs-plugins/witherchat-obs-installer/" + id + "/" + item.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
                temporaryFiles.Add(stage);
                File.WriteAllBytes(stage, item.Value);
                targets.Add((item.Key, stage));
            }
            var destinationExe = WindowsObsInstallation.OwnedPath(root, ObsPluginPayload.ExePath);
            if (!string.Equals(Path.GetFullPath(sourceExe), destinationExe, StringComparison.OrdinalIgnoreCase))
            {
                var stage = Path.Combine(staging, "WitherChat.exe");
                temporaryFiles.Add(stage);
                File.Copy(sourceExe, stage);
                if (ObsPluginPayload.Hash(stage) != ObsPluginPayload.Hash(sourceExe))
                    throw new IOException("Executable changed during installation.");
                targets.Add((ObsPluginPayload.ExePath, stage));
            }
            // Check every target before changing any installed files.
            foreach (var item in targets)
            {
                var target = WindowsObsInstallation.OwnedPath(root, item.Relative);
                if (Directory.Exists(target)) throw new IOException("A plugin target is a directory.");
            }
            if (probe.ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
            for (var index = 0; index < targets.Count; index++)
            {
                var item = targets[index];
                var target = WindowsObsInstallation.OwnedPath(root, item.Relative);
                string? saved = null;
                if (File.Exists(target))
                {
                    saved = WindowsObsInstallation.OwnedPath(root, "data/obs-plugins/witherchat-obs-backups/" + id + "/" + item.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(target, saved);
                }
                beforeWrite?.Invoke(index);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                WindowsObsInstallation.AssertNoLinks(root, target);
                File.Move(item.Stage, target, overwrite: true);
                changed.Add((target, saved));
            }
            return new(ObsPluginInstallCode.Success);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var restored = true;
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    WindowsObsInstallation.AssertNoLinks(root, item.Target);
                    if (item.Backup is null) File.Delete(item.Target);
                    else File.Copy(item.Backup, item.Target, overwrite: true);
                }
                catch (Exception rollbackException) when (rollbackException is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { restored = false; }
            }
            return new(!restored ? ObsPluginInstallCode.RollbackFailed :
                exception is UnauthorizedAccessException ? ObsPluginInstallCode.AccessDenied : ObsPluginInstallCode.Failed);
        }
        finally
        {
            foreach (var file in temporaryFiles)
            {
                try { WindowsObsInstallation.AssertNoLinks(root, file); File.Delete(file); }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { }
            }
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
