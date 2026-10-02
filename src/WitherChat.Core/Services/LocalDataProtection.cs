using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WitherChat.Core.Services;

internal static class LocalDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy) => Crypt(data, entropy, true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Crypt(data, entropy, false);

    private static byte[] Crypt(byte[] data, byte[] entropy, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entropy);
        var input = CreateBlob(data);
        var optionalEntropy = CreateBlob(entropy);
        var output = new DataBlob();
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref input, "WitherChat token", ref optionalEntropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.CbData];
            Marshal.Copy(output.PbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(optionalEntropy);
            if (output.PbData != IntPtr.Zero)
            {
                _ = LocalFree(output.PbData);
            }
        }
    }

    private static DataBlob CreateBlob(byte[] data)
    {
        var blob = new DataBlob { CbData = data.Length, PbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        if (data.Length > 0)
        {
            Marshal.Copy(data, 0, blob.PbData, data.Length);
        }

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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
