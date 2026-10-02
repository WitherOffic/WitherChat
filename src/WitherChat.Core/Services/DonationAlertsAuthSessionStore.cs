using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class DonationAlertsAuthSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDataPaths _paths;
    private readonly ISecureTokenStore _secureStore;
    private readonly object _fileGate = new();

    public DonationAlertsAuthSessionStore(AppDataPaths paths, ISecureTokenStore? secureStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _secureStore = secureStore ?? SecureTokenStoreFactory.CreateDonationAlerts(paths);
    }

    public bool IsPersistent => _secureStore.IsPersistent;

    public DonationAlertsAuthSession? Load()
    {
        lock (_fileGate)
        {
            try
            {
                if (File.Exists(_paths.DonationAlertsTokenLogoutMarker))
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
                    return JsonSerializer.Deserialize<DonationAlertsAuthSession>(data, JsonOptions);
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

    public void Save(DonationAlertsAuthSession session)
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
                    throw new CryptographicException("No secure DonationAlerts session storage is available.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
            if (File.Exists(_paths.DonationAlertsTokenLogoutMarker))
            {
                File.Delete(_paths.DonationAlertsTokenLogoutMarker);
            }
        }
    }

    public void Clear()
    {
        lock (_fileGate)
        {
            _paths.EnsureCreated();
            File.WriteAllText(_paths.DonationAlertsTokenLogoutMarker, "logged-out", Encoding.ASCII);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    _paths.DonationAlertsTokenLogoutMarker,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            _secureStore.Clear();
        }
    }
}
