using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WitherChat.Services;

internal static class LocalDataProtection
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(byte[] data, byte[] entropy, string description) =>
        Crypt(data, entropy, description, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) =>
        Crypt(data, entropy, string.Empty, protect: false);

    private static byte[] Crypt(byte[] data, byte[] entropy, string description, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(entropy);

        var inputBlob = CreateBlob(data);
        var entropyBlob = CreateBlob(entropy);
        var outputBlob = new DataBlob();

        try
        {
            var ok = protect
                ? CryptProtectData(
                    ref inputBlob,
                    description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    ref outputBlob);

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
        blob.PbData = Marshal.AllocHGlobal(Math.Max(1, data.Length));
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
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
