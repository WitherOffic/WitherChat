using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WitherChat.Core.Services;

public static class SecureTokenStoreFactory
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WitherChat.TwitchSession.v1");

    public static ISecureTokenStore Create(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiTokenStore(paths.TokenFile, Entropy);
        }

        ISecureTokenStore? persistent = null;
        if (OperatingSystem.IsMacOS())
        {
            persistent = new MacOsKeychainTokenStore("WitherChat", Environment.UserName);
        }
        else if (OperatingSystem.IsLinux() && LinuxSecretServiceTokenStore.IsCommandAvailable())
        {
            persistent = new LinuxSecretServiceTokenStore("WitherChat", Environment.UserName);
        }

        return persistent is null
            ? new MemoryTokenStore()
            : new FallbackTokenStore(persistent, new MemoryTokenStore());
    }

    public static ISecureTokenStore CreateYouTube(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var entropy = Encoding.UTF8.GetBytes("WitherChat.YouTubeSession.v1");
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiTokenStore(paths.YouTubeTokenFile, entropy);
        }

        ISecureTokenStore? persistent = null;
        if (OperatingSystem.IsMacOS())
        {
            persistent = new MacOsKeychainTokenStore("WitherChat.YouTube", Environment.UserName);
        }
        else if (OperatingSystem.IsLinux() && LinuxSecretServiceTokenStore.IsCommandAvailable())
        {
            persistent = new LinuxSecretServiceTokenStore("WitherChat.YouTube", Environment.UserName);
        }

        return persistent is null
            ? new MemoryTokenStore()
            : new FallbackTokenStore(persistent, new MemoryTokenStore());
    }

    public static ISecureTokenStore CreateDonationAlerts(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var entropy = Encoding.UTF8.GetBytes("WitherChat.DonationAlertsSession.v1");
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiTokenStore(paths.DonationAlertsTokenFile, entropy);
        }

        ISecureTokenStore? persistent = null;
        if (OperatingSystem.IsMacOS())
        {
            persistent = new MacOsKeychainTokenStore("WitherChat.DonationAlerts", Environment.UserName);
        }
        else if (OperatingSystem.IsLinux() && LinuxSecretServiceTokenStore.IsCommandAvailable())
        {
            persistent = new LinuxSecretServiceTokenStore("WitherChat.DonationAlerts", Environment.UserName);
        }

        return persistent is null
            ? new MemoryTokenStore()
            : new FallbackTokenStore(persistent, new MemoryTokenStore());
    }

    public static ISecureTokenStore CreateDonationAlertsWidget(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var entropy = Encoding.UTF8.GetBytes("WitherChat.DonationAlertsWidget.v1");
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiTokenStore(paths.DonationAlertsWidgetTokenFile, entropy);
        }

        ISecureTokenStore? persistent = null;
        if (OperatingSystem.IsMacOS())
        {
            persistent = new MacOsKeychainTokenStore("WitherChat.DonationAlertsWidget", Environment.UserName);
        }
        else if (OperatingSystem.IsLinux() && LinuxSecretServiceTokenStore.IsCommandAvailable())
        {
            persistent = new LinuxSecretServiceTokenStore("WitherChat.DonationAlertsWidget", Environment.UserName);
        }

        return persistent is null
            ? new MemoryTokenStore()
            : new FallbackTokenStore(persistent, new MemoryTokenStore());
    }

    private sealed class WindowsDpapiTokenStore(string filePath, byte[] entropy) : ISecureTokenStore
    {
        public bool IsPersistent => true;

        public byte[]? Load()
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            return LocalDataProtection.Unprotect(File.ReadAllBytes(filePath), entropy);
        }

        public bool TrySave(byte[] data)
        {
            var protectedData = LocalDataProtection.Protect(data, entropy);
            WriteAtomically(filePath, protectedData, restrictToCurrentUser: false);
            return true;
        }

        public void Clear() => TryDelete(filePath);
    }

    private sealed class MemoryTokenStore : ISecureTokenStore
    {
        private byte[]? _data;
        public bool IsPersistent => false;
        public byte[]? Load() => _data?.ToArray();

        public bool TrySave(byte[] data)
        {
            _data = data.ToArray();
            return true;
        }

        public void Clear()
        {
            if (_data is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(_data);
                _data = null;
            }
        }
    }

    private sealed class FallbackTokenStore(ISecureTokenStore persistent, ISecureTokenStore fallback)
        : ISecureTokenStore
    {
        private int _persistentAvailable = persistent.IsPersistent ? 1 : 0;
        public bool IsPersistent => Volatile.Read(ref _persistentAvailable) != 0;

        public byte[]? Load()
        {
            var stored = persistent.Load();
            if (stored is not null)
            {
                Volatile.Write(ref _persistentAvailable, 1);
                return stored;
            }
            var temporary = fallback.Load();
            if (temporary is not null)
            {
                Volatile.Write(ref _persistentAvailable, 0);
            }
            return temporary;
        }

        public bool TrySave(byte[] data)
        {
            if (persistent.TrySave(data))
            {
                Volatile.Write(ref _persistentAvailable, 1);
                fallback.Clear();
                return true;
            }
            Volatile.Write(ref _persistentAvailable, 0);
            return fallback.TrySave(data);
        }

        public void Clear()
        {
            persistent.Clear();
            fallback.Clear();
        }
    }

    private sealed class LinuxSecretServiceTokenStore(string service, string account) : ISecureTokenStore
    {
        public bool IsPersistent => true;

        public static bool IsCommandAvailable()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            return !string.IsNullOrWhiteSpace(path) && path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(directory => File.Exists(Path.Combine(directory, "secret-tool")));
        }

        public byte[]? Load()
        {
            var result = RunSecretTool(["lookup", "service", service, "account", account], null);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(result.Output.Trim());
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public bool TrySave(byte[] data)
        {
            var result = RunSecretTool(
                ["store", "--label=WitherChat", "service", service, "account", account],
                Convert.ToBase64String(data));
            return result.ExitCode == 0;
        }

        public void Clear() =>
            _ = RunSecretTool(["clear", "service", service, "account", account], null);

        private static ProcessResult RunSecretTool(IReadOnlyList<string> arguments, string? standardInput)
        {
            try
            {
                var startInfo = new ProcessStartInfo("secret-tool")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = standardInput is not null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return new ProcessResult(-1, string.Empty);
                }
                if (standardInput is not null)
                {
                    process.StandardInput.Write(standardInput);
                    process.StandardInput.Close();
                }
                var output = process.StandardOutput.ReadToEnd();
                _ = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    return new ProcessResult(-1, string.Empty);
                }
                return new ProcessResult(process.ExitCode, output);
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException or IOException)
            {
                return new ProcessResult(-1, string.Empty);
            }
        }
    }

    private sealed class MacOsKeychainTokenStore(string service, string account) : ISecureTokenStore
    {
        private const int Success = 0;
        private const int ItemNotFound = -25300;
        private readonly byte[] _service = Encoding.UTF8.GetBytes(service);
        private readonly byte[] _account = Encoding.UTF8.GetBytes(account);
        public bool IsPersistent => true;

        public byte[]? Load()
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)_service.Length),
                _service,
                checked((uint)_account.Length),
                _account,
                out var length,
                out var data,
                out var item);
            try
            {
                if (status != Success || data == IntPtr.Zero)
                {
                    return null;
                }
                var result = new byte[checked((int)length)];
                Marshal.Copy(data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (data != IntPtr.Zero)
                {
                    _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
                }
                if (item != IntPtr.Zero)
                {
                    CFRelease(item);
                }
            }
        }

        public bool TrySave(byte[] data)
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)_service.Length),
                _service,
                checked((uint)_account.Length),
                _account,
                out _,
                out var existingData,
                out var item);
            if (existingData != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, existingData);
            }
            try
            {
                if (status == Success && item != IntPtr.Zero)
                {
                    return SecKeychainItemModifyAttributesAndData(
                        item,
                        IntPtr.Zero,
                        checked((uint)data.Length),
                        data) == Success;
                }
                if (status != ItemNotFound)
                {
                    return false;
                }
                return SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    checked((uint)_service.Length),
                    _service,
                    checked((uint)_account.Length),
                    _account,
                    checked((uint)data.Length),
                    data,
                    out item) == Success;
            }
            finally
            {
                if (item != IntPtr.Zero)
                {
                    CFRelease(item);
                }
            }
        }

        public void Clear()
        {
            var status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)_service.Length),
                _service,
                checked((uint)_account.Length),
                _account,
                out _,
                out var data,
                out var item);
            if (data != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
            try
            {
                if (status == Success && item != IntPtr.Zero)
                {
                    _ = SecKeychainItemDelete(item);
                }
            }
            finally
            {
                if (item != IntPtr.Zero)
                {
                    CFRelease(item);
                }
            }
        }

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemRef);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemModifyAttributesAndData(
            IntPtr itemRef,
            IntPtr attributeList,
            uint length,
            byte[] data);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemFreeContent(IntPtr attributeList, IntPtr data);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRelease(IntPtr value);
    }

    private static void WriteAtomically(string path, byte[] data, bool restrictToCurrentUser)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            if (restrictToCurrentUser && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}
