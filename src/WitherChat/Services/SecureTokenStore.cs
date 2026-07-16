using System.IO;
using System.Text;
using System.Text.Json;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class SecureTokenStore
{
    private static readonly object FileGate = new();
    // Preserve access to tokens created before the project rename.
    private static readonly byte[] Entropy = CreateLegacyCompatibleEntropy();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static byte[] CreateLegacyCompatibleEntropy()
    {
        ReadOnlySpan<byte> encoded =
        [
            14, 45, 51, 46, 57, 50, 25, 50, 59, 46, 23, 44, 42, 116,
            14, 53, 49, 63, 52, 9, 46, 53, 40, 63, 116, 44, 107
        ];
        var decoded = new byte[encoded.Length];
        for (var index = 0; index < encoded.Length; index++)
        {
            decoded[index] = (byte)(encoded[index] ^ 0x5A);
        }

        return decoded;
    }

    public TwitchTokenSet? Load()
    {
        lock (FileGate)
        {
            try
            {
                if (File.Exists(AppPaths.TokenLogoutMarker))
                {
                    return null;
                }

                AppPaths.TryMigrateLegacyFile(AppPaths.LegacyTokenFile, AppPaths.TokenFile);
                if (!File.Exists(AppPaths.TokenFile))
                {
                    return null;
                }

                var encrypted = File.ReadAllBytes(AppPaths.TokenFile);
                var data = LocalDataProtection.Unprotect(encrypted, Entropy);
                var json = Encoding.UTF8.GetString(data);
                return JsonSerializer.Deserialize<TwitchTokenSet>(json, JsonOptions);
            }
            catch
            {
                // A transient read/DPAPI failure must not destroy a potentially valid token.
                // A successful sign-in will atomically replace an unreadable token file.
                return null;
            }
        }
    }

    public void Save(TwitchTokenSet tokenSet)
    {
        lock (FileGate)
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            var json = JsonSerializer.Serialize(tokenSet);
            var data = Encoding.UTF8.GetBytes(json);
            var encrypted = LocalDataProtection.Protect(data, Entropy, "WitherChat token");
            var tempPath = AppPaths.TokenFile + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, encrypted);
                File.Move(tempPath, AppPaths.TokenFile, overwrite: true);
                if (File.Exists(AppPaths.TokenLogoutMarker))
                {
                    File.Delete(AppPaths.TokenLogoutMarker);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }

    public void Clear()
    {
        lock (FileGate)
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            File.WriteAllText(AppPaths.TokenLogoutMarker, "logged-out", Encoding.ASCII);

            TryDeleteTokenFile(AppPaths.TokenFile);
            TryDeleteTokenFile(AppPaths.LegacyTokenFile);
        }
    }

    private static void TryDeleteTokenFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The durable logout marker prevents reuse even if an encrypted token file is temporarily locked.
        }
    }

}
