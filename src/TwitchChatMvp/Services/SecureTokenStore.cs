using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class SecureTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TwitchChatMvp.TokenStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TwitchTokenSet? Load()
    {
        try
        {
            AppPaths.TryMigrateLegacyFile(AppPaths.LegacyTokenFile, AppPaths.TokenFile);
            if (!File.Exists(AppPaths.TokenFile))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(AppPaths.TokenFile);
            var data = Dpapi.Unprotect(encrypted, Entropy);
            var json = Encoding.UTF8.GetString(data);
            return JsonSerializer.Deserialize<TwitchTokenSet>(json, JsonOptions);
        }
        catch
        {
            Clear();
            return null;
        }
    }

    public void Save(TwitchTokenSet tokenSet)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var json = JsonSerializer.Serialize(tokenSet);
        var data = Encoding.UTF8.GetBytes(json);
        var encrypted = Dpapi.Protect(data, Entropy);
        File.WriteAllBytes(AppPaths.TokenFile, encrypted);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.TokenFile))
            {
                File.Delete(AppPaths.TokenFile);
            }

            if (File.Exists(AppPaths.LegacyTokenFile))
            {
                File.Delete(AppPaths.LegacyTokenFile);
            }
        }
        catch
        {
            // If Windows keeps the file locked for a moment, auth will simply ask to reconnect later.
        }
    }

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] data, byte[] entropy) => Crypt(data, entropy, protect: true);
        public static byte[] Unprotect(byte[] data, byte[] entropy) => Crypt(data, entropy, protect: false);

        private static byte[] Crypt(byte[] data, byte[] entropy, bool protect)
        {
            var inputBlob = CreateBlob(data);
            var entropyBlob = CreateBlob(entropy);
            var outputBlob = new DataBlob();

            try
            {
                var ok = protect
                    ? CryptProtectData(ref inputBlob, "TwitchChatMvp", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);

                if (!ok)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var output = new byte[outputBlob.CbData];
                Marshal.Copy(outputBlob.PbData, output, 0, output.Length);
                return output;
            }
            finally
            {
                FreeBlob(inputBlob);
                FreeBlob(entropyBlob);
                if (outputBlob.PbData != IntPtr.Zero)
                {
                    LocalFree(outputBlob.PbData);
                }
            }
        }

        private static DataBlob CreateBlob(byte[] data)
        {
            var blob = new DataBlob { CbData = data.Length };
            blob.PbData = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, blob.PbData, data.Length);
            return blob;
        }

        private static void FreeBlob(DataBlob blob)
        {
            if (blob.PbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(blob.PbData);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int CbData;
            public IntPtr PbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn,
            string? szDataDescr,
            ref DataBlob pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn,
            IntPtr ppszDataDescr,
            ref DataBlob pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);
    }
}
