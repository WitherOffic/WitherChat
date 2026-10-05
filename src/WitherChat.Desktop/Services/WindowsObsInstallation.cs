using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WitherChat.Desktop.Services;

internal interface IObsInstallationProbe
{
    string ProductVersion(string file);
    ushort PeMachine(string file);
    bool ObsRunning();
}

internal sealed class WindowsObsInstallation : IObsInstallationProbe
{
    internal static bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    public string ProductVersion(string file) => FileVersionInfo.GetVersionInfo(file).ProductVersion ?? string.Empty;
    public ushort PeMachine(string file)
    {
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[64];
        if (stream.Length < header.Length) return 0;
        stream.ReadExactly(header);
        if (header[0] != 0x4d || header[1] != 0x5a) return 0;
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
        if (offset < 64 || offset > stream.Length - 6) return 0;
        stream.Position = offset;
        Span<byte> pe = stackalloc byte[6];
        stream.ReadExactly(pe);
        return pe[..4].SequenceEqual("PE\0\0"u8)
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) : (ushort)0;
    }
    public bool ObsRunning()
    {
        var processes = Process.GetProcessesByName("obs64");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    internal static ushort GetPeMachine(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || data[0] != 0x4d || data[1] != 0x5a) return 0;
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[60..]);
        if (offset < 64 || offset > data.Length - 6 || !data.Slice(offset, 4).SequenceEqual("PE\0\0"u8)) return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
    }
    internal static string Validate(string directory, IObsInstallationProbe probe, bool requireCompatible = true)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) ||
            directory.Contains('\0')) throw new InvalidDataException("Select a local OBS installation folder.");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            root.Length <= (Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar).Length ?? 0))
            throw new InvalidDataException("OBS folder not found.");
        for (string? ancestor = root; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer does not follow symbolic links or junctions.");
        AssertNoLinks(root, root);
        var exe = Path.Combine(root, "bin", "64bit", "obs64.exe");
        var qt = Path.Combine(root, "bin", "64bit", "Qt6Core.dll");
        AssertNoLinks(root, exe);
        if (!requireCompatible)
        {
            if (!File.Exists(exe) || probe.PeMachine(exe) != 0x8664) throw new InvalidDataException("Not an OBS x64 folder.");
            return root;
        }
        AssertNoLinks(root, qt);
        if (!File.Exists(exe) || !File.Exists(qt) || probe.ProductVersion(exe) != "32.2.2" ||
            probe.ProductVersion(qt) != "6.11.1.0" || probe.PeMachine(exe) != 0x8664 ||
            probe.PeMachine(qt) != 0x8664)
            throw new InvalidDataException("Requires OBS 32.2.2 x64 / Qt 6.11.1.");
        return root;
    }
    internal static string OwnedPath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer path escaped OBS.");
        AssertNoLinks(root, path);
        return path;
    }
    internal static void AssertNoLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer does not follow symbolic links or junctions.");
            if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Invalid installer boundary.");
            if (current.Length < boundary.Length) throw new InvalidDataException("Invalid installer boundary.");
        }
    }

    internal static IReadOnlyList<string> FindCandidates()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return [];
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var path = Path.GetFullPath(value.Trim().Trim('"'));
                if (File.Exists(Path.Combine(path, "bin", "64bit", "obs64.exe"))) candidates.Add(path);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { }
        }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio"));
        foreach (var process in Process.GetProcessesByName("obs64"))
        {
            using (process)
            {
                try { Add(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(process.MainModule?.FileName)))); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var obs = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio");
            Add(obs?.GetValue("InstallLocation") as string);
            using var steam = machine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (steam?.GetValue("InstallPath") is string root) steamRoots.Add(root);
        }
        using (var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            if (user?.GetValue("SteamPath") is string root) steamRoots.Add(root);
        foreach (var steam in steamRoots.ToArray())
        {
            try
            {
                var libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libraries) && new FileInfo(libraries).Length < 1024 * 1024)
                    foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s*\"([^\"]+)\"",
                                 RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                        steamRoots.Add(match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
        }
        foreach (var steam in steamRoots) Add(Path.Combine(steam, "steamapps", "common", "OBS Studio"));
        return candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
