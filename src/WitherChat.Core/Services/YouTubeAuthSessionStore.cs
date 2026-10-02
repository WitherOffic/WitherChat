using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class YouTubeAuthSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDataPaths _paths;
    private readonly ISecureTokenStore _secureStore;
    private readonly object _fileGate = new();

    public YouTubeAuthSessionStore(AppDataPaths paths, ISecureTokenStore? secureStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secureStore = secureStore ?? SecureTokenStoreFactory.CreateYouTube(paths);
    }

    public bool IsPersistent => _secureStore.IsPersistent;

    public YouTubeAuthSession? Load()
    {
        lock (_fileGate)
        {
            try
            {
                if (File.Exists(_paths.YouTubeTokenLogoutMarker))
                {
                    return null;
                }

                var data = _secureStore.Load();
                if (data is null)
                {
                    return null;
                }
                try
                {
                    return JsonSerializer.Deserialize<YouTubeAuthSession>(data, JsonOptions);
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

    public void Save(YouTubeAuthSession session)
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
                    throw new CryptographicException("No secure YouTube session storage is available.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
            if (File.Exists(_paths.YouTubeTokenLogoutMarker))
            {
                File.Delete(_paths.YouTubeTokenLogoutMarker);
            }
        }
    }

    public void Clear()
    {
        lock (_fileGate)
        {
            _paths.EnsureCreated();
            File.WriteAllText(_paths.YouTubeTokenLogoutMarker, "logged-out", Encoding.ASCII);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    _paths.YouTubeTokenLogoutMarker,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            _secureStore.Clear();
        }
    }
}
