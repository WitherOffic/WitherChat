using System.Security.Cryptography;
using System.Text;

namespace WitherChat.Core.Services;

internal static class PortableDataProtection
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("WCP1");
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static bool IsProtected(ReadOnlySpan<byte> data) =>
        data.Length >= Header.Length && data[..Header.Length].SequenceEqual(Header);

    public static byte[] Protect(byte[] data, byte[] associatedData, string keyFile)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(associatedData);
        var key = LoadOrCreateKey(keyFile);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[data.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Encrypt(nonce, data, ciphertext, tag, associatedData);
            }

            var result = new byte[Header.Length + NonceSize + TagSize + ciphertext.Length];
            Header.CopyTo(result, 0);
            nonce.CopyTo(result, Header.Length);
            tag.CopyTo(result, Header.Length + NonceSize);
            ciphertext.CopyTo(result, Header.Length + NonceSize + TagSize);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Unprotect(byte[] stored, byte[] associatedData, string keyFile)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(associatedData);
        if (!IsProtected(stored) || stored.Length < Header.Length + NonceSize + TagSize)
        {
            throw new CryptographicException("The WitherChat session envelope is invalid.");
        }

        var key = File.ReadAllBytes(keyFile);
        try
        {
            if (key.Length != KeySize)
            {
                throw new CryptographicException("The WitherChat session key is invalid.");
            }

            var nonce = stored.AsSpan(Header.Length, NonceSize);
            var tag = stored.AsSpan(Header.Length + NonceSize, TagSize);
            var ciphertext = stored.AsSpan(Header.Length + NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] LoadOrCreateKey(string keyFile)
    {
        if (File.Exists(keyFile))
        {
            var existing = File.ReadAllBytes(keyFile);
            if (existing.Length != KeySize)
            {
                throw new CryptographicException("The WitherChat session key is invalid.");
            }

            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var temporaryFile = keyFile + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryFile, key);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            try
            {
                File.Move(temporaryFile, keyFile);
            }
            catch (IOException) when (File.Exists(keyFile))
            {
                var existing = File.ReadAllBytes(keyFile);
                if (existing.Length != KeySize)
                {
                    throw new CryptographicException("The WitherChat session key is invalid.");
                }

                CryptographicOperations.ZeroMemory(key);
                return existing;
            }

            return key;
        }
        finally
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
    }
}
