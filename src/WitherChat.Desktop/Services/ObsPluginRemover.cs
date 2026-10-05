using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WitherChat.Desktop.Services;

internal sealed class ObsPluginRemover(IObsInstallationProbe probe, string sourceExe)
{
    private static readonly Regex OwnedId = new(@"^\d{8}-\d{6}-[a-f0-9]{32}$", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    internal static readonly string[] ActiveFiles = [.. ObsPluginPayload.RequiredPaths, ObsPluginPayload.ExePath];

    internal static IReadOnlyList<string> FindFiles(string root)
    {
        var files = new List<string>();
        void Add(string relative)
        {
            var file = WindowsObsInstallation.OwnedPath(root, relative);
            if (Directory.Exists(file)) throw new InvalidDataException("A plugin file is a directory.");
            if (File.Exists(file)) files.Add(relative);
        }
        foreach (var relative in ActiveFiles) Add(relative);
        foreach (var group in new[] { "witherchat-obs-backups", "witherchat-obs-installer" })
        {
            var prefix = "data/obs-plugins/" + group + "/";
            var directory = WindowsObsInstallation.OwnedPath(root, prefix);
            if (!Directory.Exists(directory)) continue;
            var ids = Directory.EnumerateDirectories(directory).ToArray();
            if (ids.Length > 1000) throw new IOException("Too many backup folders.");
            foreach (var idDirectory in ids)
            {
                var id = Path.GetFileName(idDirectory);
                if (!OwnedId.IsMatch(id)) continue;
                WindowsObsInstallation.AssertNoLinks(root, idDirectory);
                foreach (var relative in group == "witherchat-obs-backups" ? ActiveFiles : ObsPluginPayload.RequiredPaths)
                    Add(prefix + id + "/" + relative);
                if (group == "witherchat-obs-installer") Add(prefix + id + "/WitherChat.exe");
            }
        }
        return files;
    }

    internal ObsPluginInstallResult Remove(string directory, Action<int>? beforeDelete = null)
    {
        string root;
        try { root = WindowsObsInstallation.Validate(directory, probe, requireCompatible: false); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        { return new(ObsPluginInstallCode.InvalidTarget); }
        if (probe.ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
        if (string.Equals(Path.GetFullPath(sourceExe), WindowsObsInstallation.OwnedPath(root, ObsPluginPayload.ExePath),
                StringComparison.OrdinalIgnoreCase)) return new(ObsPluginInstallCode.RunningFromPlugin);
        using var mutex = new Mutex(false, @"Global\WitherChat.ObsInstaller." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return new(ObsPluginInstallCode.Busy);
            var files = FindFiles(root); // Validate all destinations before removing anything.
            if (probe.ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
            for (var index = 0; index < files.Count; index++)
            {
                if (probe.ObsRunning()) return new(ObsPluginInstallCode.ObsRunning);
                var file = WindowsObsInstallation.OwnedPath(root, files[index]);
                beforeDelete?.Invoke(index);
                if (!File.Exists(file)) continue;
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }
            RemoveEmptyOwnedDirectories(root);
            return new(ObsPluginInstallCode.Success);
        }
        catch (UnauthorizedAccessException) { return new(ObsPluginInstallCode.AccessDenied); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        { return new(ObsPluginInstallCode.Failed); }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static void RemoveEmptyOwnedDirectories(string root)
    {
        void Empty(string relative)
        {
            var directory = WindowsObsInstallation.OwnedPath(root, relative);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false); }
            catch (IOException) { } // Unknown files, even inside our folder, must be retained.
        }
        void Tree(string prefix)
        {
            Empty(prefix + "/Assets/Fonts"); Empty(prefix + "/Assets"); Empty(prefix);
        }
        Tree(ObsPluginPayload.DataPath.TrimEnd('/'));
        foreach (var group in new[] { "witherchat-obs-backups", "witherchat-obs-installer" })
        {
            var prefix = "data/obs-plugins/" + group;
            var directory = WindowsObsInstallation.OwnedPath(root, prefix);
            if (!Directory.Exists(directory)) continue;
            foreach (var idDirectory in Directory.EnumerateDirectories(directory).ToArray())
            {
                var id = Path.GetFileName(idDirectory);
                if (!OwnedId.IsMatch(id)) continue;
                var child = prefix + "/" + id;
                Tree(child + "/" + ObsPluginPayload.DataPath.TrimEnd('/'));
                Empty(child + "/data/obs-plugins"); Empty(child + "/data");
                Empty(child + "/obs-plugins/64bit"); Empty(child + "/obs-plugins"); Empty(child);
            }
            Empty(prefix);
        }
    }
}
