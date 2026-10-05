using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace WitherChat.Desktop.Services;

internal sealed class ObsPluginPayload
{
    internal const string ResourceName = "WitherChat.ObsPlugin.WindowsX64.zip";
    internal const string DllPath = "obs-plugins/64bit/witherchat-obs.dll";
    internal const string DataPath = "data/obs-plugins/witherchat-obs/";
    internal const string ExePath = DataPath + "WitherChat.exe";
    internal static readonly string[] RequiredPaths =
    [
        DllPath, DataPath + "LICENSE", DataPath + "THIRD-PARTY-NOTICES.md",
        DataPath + "DOTNET-RUNTIME-LICENSE.txt", DataPath + "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
        DataPath + "SKIASHARP-HARFBUZZ-THIRD-PARTY-NOTICES.txt", DataPath + "Assets/Fonts/OFL-Inter.txt",
        DataPath + "OBS-PLUGIN-LICENSE.txt", DataPath + "plugin-source.zip"
    ];
    internal IReadOnlyDictionary<string, byte[]> Files { get; }
    private ObsPluginPayload(Dictionary<string, byte[]> files) => Files = files;

    internal static ObsPluginPayload? LoadEmbedded()
    {
        using var stream = typeof(ObsPluginPayload).Assembly.GetManifestResourceStream(ResourceName);
        return stream is null ? null : Read(stream);
    }

    internal static ObsPluginPayload Read(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var allowed = new HashSet<string>(RequiredPaths, StringComparer.Ordinal);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!allowed.Contains(entry.FullName) || files.ContainsKey(entry.FullName) ||
                entry.Length is <= 0 or > 8 * 1024 * 1024 || (total += entry.Length) > 16 * 1024 * 1024)
                throw new InvalidDataException("Invalid OBS installer payload.");
            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > entry.Length) throw new InvalidDataException("Payload size mismatch.");
                output.Write(buffer, 0, count);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("Truncated payload.");
            files.Add(entry.FullName, output.ToArray());
        }
        if (files.Count != allowed.Count || WindowsObsInstallation.GetPeMachine(files[DllPath]) != 0x8664 ||
            System.Text.Encoding.ASCII.GetString(files[DllPath]).Contains("native probe completed", StringComparison.Ordinal))
            throw new InvalidDataException("Missing files or non-production DLL.");
        return new ObsPluginPayload(files);
    }

    internal bool Matches(string relative, string file) => File.Exists(file) &&
        Hash(file) == Convert.ToHexString(SHA256.HashData(Files[relative]));

    internal static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "Empty assembly location intentionally identifies the distributable single-file executable.")]
    internal static string? CurrentSingleFileExe()
    {
        var path = Environment.ProcessPath;
        return OperatingSystem.IsWindows() && string.IsNullOrEmpty(typeof(Program).Assembly.Location) &&
            path is not null && File.Exists(path) ? path : null;
    }
}
