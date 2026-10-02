using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class TwitchAuthSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] LegacyPortableEntropy = Encoding.UTF8.GetBytes("WitherChat.TwitchSession.v1");
    private readonly AppDataPaths _paths;
    private readonly ISecureTokenStore _secureStore;
    private readonly object _fileGate = new();

    public TwitchAuthSessionStore(AppDataPaths paths, ISecureTokenStore? secureStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secureStore = secureStore ?? SecureTokenStoreFactory.Create(paths);
    }

    public bool IsPersistent => _secureStore.IsPersistent;

    public TwitchAuthSession? Load()
    {
        lock (_fileGate)
        {
            try
            {
                if (File.Exists(_paths.TokenLogoutMarker))
                {
                    return null;
                }

                var data = _secureStore.Load();
                if (data is null)
                {
                    var migrated = TryLoadLegacySession() ?? TryLoadPortableSession();
                    if (migrated is not null)
                    {
                        Save(migrated);
                    }
                    return migrated;
                }
                try
                {
                    return JsonSerializer.Deserialize<TwitchAuthSession>(data, JsonOptions);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(data);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or Win32Exception or CryptographicException)
            {
                return null;
            }
        }
    }

    private TwitchAuthSession? TryLoadPortableSession()
    {
        if (OperatingSystem.IsWindows() ||
            !File.Exists(_paths.TokenFile) ||
            !File.Exists(_paths.SecretKeyFile))
        {
            return null;
        }

        var stored = File.ReadAllBytes(_paths.TokenFile);
        if (!PortableDataProtection.IsProtected(stored))
        {
            return null;
        }
        var data = PortableDataProtection.Unprotect(
            stored,
            LegacyPortableEntropy,
            _paths.SecretKeyFile);
        try
        {
            return JsonSerializer.Deserialize<TwitchAuthSession>(data, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private TwitchAuthSession? TryLoadLegacySession()
    {
        if (!OperatingSystem.IsWindows() ||
            File.Exists(_paths.LegacyTokenLogoutMarker) ||
            !File.Exists(_paths.LegacyTokenFile))
        {
            return null;
        }
        var encrypted = File.ReadAllBytes(_paths.LegacyTokenFile);
        var data = LocalDataProtection.Unprotect(encrypted, CreateLegacyEntropy());
        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyTokenSet>(data, JsonOptions);
            if (legacy is null || string.IsNullOrWhiteSpace(legacy.AccessToken))
            {
                return null;
            }
            return new TwitchAuthSession(
                legacy.AccessToken,
                legacy.RefreshToken ?? string.Empty,
                legacy.ClientId,
                legacy.UserId ?? string.Empty,
                legacy.Login ?? string.Empty,
                legacy.Scopes ?? [],
                legacy.ExpiresAtUtc,
                legacy.LastValidatedAtUtc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[] CreateLegacyEntropy()
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

    public void Save(TwitchAuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_fileGate)
        {
            _paths.EnsureCreated();
            var data = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
            try
            {
                if (!_secureStore.TrySave(data))
                {
                    throw new CryptographicException("No secure Twitch session storage is available.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
            if (File.Exists(_paths.TokenLogoutMarker))
            {
                File.Delete(_paths.TokenLogoutMarker);
            }
        }
    }

    public void Clear()
    {
        lock (_fileGate)
        {
            _paths.EnsureCreated();
            File.WriteAllText(_paths.TokenLogoutMarker, "logged-out", Encoding.ASCII);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    _paths.TokenLogoutMarker,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            _secureStore.Clear();
        }
    }

    private sealed class LegacyTokenSet
    {
        public string ClientId { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public List<string>? Scopes { get; set; }
        public string? UserId { get; set; }
        public string? Login { get; set; }
        public DateTimeOffset LastValidatedAtUtc { get; set; }
    }
}
