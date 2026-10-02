using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class DonationAlertsObsController : IDonationAlertsObsController, IDisposable
{
    private const long MaximumSceneCollectionBytes = 32 * 1024 * 1024;
    private const string DonationAlertsHost = "www.donationalerts.com";
    private const string AlertsWidgetPath = "/widget/alerts";
    private static readonly TimeSpan DefaultStableReadyDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultInitialReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultReadyWaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(15);
    private readonly IDonationAlertsWidgetSocketClient _socketClient;
    private readonly bool _ownsSocketClient;
    private readonly ISecureTokenStore _widgetTokenStore;
    private readonly string _obsScenesDirectory;
    private readonly object _configurationGate = new();
    private readonly object _reconnectGate = new();
    private readonly CancellationTokenSource _reconnectCancellation = new();
    private readonly List<WidgetTokenCandidate> _widgetTokenCandidates = [];
    private readonly HashSet<string> _rejectedWidgetTokens = new(StringComparer.Ordinal);
    private readonly TimeSpan _stableReadyDuration;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _readyWaitTimeout;
    private string _widgetToken = string.Empty;
    private string _sourceName = string.Empty;
    private string _lastKnownGoodToken = string.Empty;
    private TaskCompletionSource _readySignal = CreateReadySignal();
    private Task? _reconnectTask;
    private bool _disposed;

    public DonationAlertsObsController(
        string? obsScenesDirectory = null)
        : this(
            obsScenesDirectory,
            null,
            SecureTokenStoreFactory.CreateDonationAlertsWidget(new AppDataPaths()))
    {
    }

    public DonationAlertsObsController(
        AppDataPaths paths,
        string? obsScenesDirectory = null)
        : this(
            obsScenesDirectory,
            null,
            SecureTokenStoreFactory.CreateDonationAlertsWidget(paths))
    {
    }

    internal DonationAlertsObsController(
        string? obsScenesDirectory,
        IDonationAlertsWidgetSocketClient? socketClient,
        ISecureTokenStore widgetTokenStore,
        TimeSpan? stableReadyDuration = null,
        TimeSpan? initialReconnectDelay = null,
        TimeSpan? readyWaitTimeout = null)
    {
        _obsScenesDirectory = string.IsNullOrWhiteSpace(obsScenesDirectory)
            ? ResolveObsScenesDirectory()
            : Path.GetFullPath(obsScenesDirectory);
        _socketClient = socketClient ?? new DonationAlertsWidgetSocketClient();
        _ownsSocketClient = socketClient is null;
        _widgetTokenStore = widgetTokenStore;
        _stableReadyDuration = stableReadyDuration ?? DefaultStableReadyDuration;
        _initialReconnectDelay = initialReconnectDelay ?? DefaultInitialReconnectDelay;
        _readyWaitTimeout = readyWaitTimeout ?? DefaultReadyWaitTimeout;
        if (_stableReadyDuration <= TimeSpan.Zero ||
            _initialReconnectDelay <= TimeSpan.Zero ||
            _readyWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stableReadyDuration),
                "DonationAlerts reconnect timings must be positive.");
        }
        _socketClient.PlaybackChanged += OnPlaybackChanged;
        _ = TryAutoConfigure();
    }

    public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;

    public bool IsConfigured
    {
        get
        {
            lock (_configurationGate)
            {
                return _widgetToken.Length > 0;
            }
        }
    }

    public string SourceName
    {
        get
        {
            lock (_configurationGate)
            {
                return _sourceName;
            }
        }
    }

    public bool IsAlertPlaying => _socketClient.IsAlertPlaying;

    public bool IsConnected => _socketClient.IsConnected;

    public string ActiveAlertId => _socketClient.ActiveAlertId > 0
        ? _socketClient.ActiveAlertId.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    public bool TryAutoConfigure()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var candidates = new List<WidgetTokenCandidate>();
        if (Directory.Exists(_obsScenesDirectory))
        {
            try
            {
                var files = Directory.EnumerateFiles(
                        _obsScenesDirectory,
                        "*.json",
                        SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .ThenBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                foreach (var info in files)
                {
                    try
                    {
                        if (info.Length is <= 0 or > MaximumSceneCollectionBytes)
                        {
                            continue;
                        }

                        using var stream = File.OpenRead(info.FullName);
                        using var document = JsonDocument.Parse(stream);
                        CollectAlertsWidgets(document.RootElement, candidates);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        // A damaged backup or a scene collection locked by OBS must not
                        // prevent the remaining active collections from being inspected.
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Fall back to the last securely cached token below.
            }
        }

        var hasStoredToken = TryLoadStoredWidgetToken(out var storedToken);
        if (hasStoredToken)
        {
            var storedIndex = candidates.FindIndex(candidate =>
                string.Equals(candidate.Token, storedToken, StringComparison.Ordinal));
            var storedCandidate = storedIndex >= 0
                ? candidates[storedIndex]
                : new WidgetTokenCandidate(storedToken, string.Empty);
            if (storedIndex >= 0)
            {
                candidates.RemoveAt(storedIndex);
            }
            candidates.Insert(0, storedCandidate);
        }

        lock (_configurationGate)
        {
            _widgetTokenCandidates.Clear();
            _widgetTokenCandidates.AddRange(candidates);
            _rejectedWidgetTokens.Clear();
            _lastKnownGoodToken = hasStoredToken ? storedToken : string.Empty;
            if (candidates.Count == 0)
            {
                _widgetToken = string.Empty;
                _sourceName = string.Empty;
                return false;
            }
            _widgetToken = candidates[0].Token;
            _sourceName = candidates[0].SourceName;
            return true;
        }
    }

    public async Task RepeatDonationAsync(
        DonationAlert donation,
        CancellationToken cancellationToken = default)
    {
        var alertId = ReadAlertId(donation);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _socketClient.StartAlertAsync(
            GetWidgetToken(),
            alertId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = GetWidgetToken();
        Task ready;
        lock (_reconnectGate)
        {
            if (_socketClient.IsConnected)
            {
                return;
            }
            if (_readySignal.Task.IsCompleted)
            {
                _readySignal = CreateReadySignal();
            }
            ready = _readySignal.Task;
            ScheduleReconnectLocked();
        }
        await ready.WaitAsync(_readyWaitTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task SkipDonationAsync(
        DonationAlert donation,
        CancellationToken cancellationToken = default)
    {
        var alertId = ReadAlertId(donation);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await _socketClient.SkipAlertAsync(
            GetWidgetToken(),
            alertId,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _reconnectCancellation.Cancel();
        lock (_configurationGate)
        {
            _widgetToken = string.Empty;
            _sourceName = string.Empty;
            _lastKnownGoodToken = string.Empty;
            _widgetTokenCandidates.Clear();
            _rejectedWidgetTokens.Clear();
        }
        lock (_reconnectGate)
        {
            _readySignal.TrySetCanceled();
        }
        _socketClient.PlaybackChanged -= OnPlaybackChanged;
        if (_ownsSocketClient && _socketClient is IDisposable disposableSocketClient)
        {
            disposableSocketClient.Dispose();
        }
        _reconnectCancellation.Dispose();
    }

    private void OnPlaybackChanged(object? sender, DonationAlertsPlaybackEventArgs eventArgs)
    {
        if (eventArgs.Action == DonationAlertsPlaybackAction.ConnectionRestored && !_disposed)
        {
            lock (_reconnectGate)
            {
                _readySignal.TrySetResult();
            }
        }
        else if (eventArgs.Action == DonationAlertsPlaybackAction.ConnectionLost && !_disposed)
        {
            lock (_reconnectGate)
            {
                if (_readySignal.Task.IsCompleted)
                {
                    _readySignal = CreateReadySignal();
                }
                ScheduleReconnectLocked();
            }
        }
        PlaybackChanged?.Invoke(this, eventArgs);
    }

    private void ScheduleReconnectLocked()
    {
        if (_reconnectTask is { IsCompleted: false })
        {
            return;
        }
        _reconnectTask = Task.Run(() => ReconnectUntilConnectedAsync(_reconnectCancellation.Token));
    }

    private async Task ReconnectUntilConnectedAsync(CancellationToken cancellationToken)
    {
        var delay = _initialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            var token = GetWidgetToken();
            try
            {
                await _socketClient.PrepareAsync(token, cancellationToken).ConfigureAwait(false);
                if (!_socketClient.IsConnected)
                {
                    throw new IOException(
                        "DonationAlerts transport opened without a confirmed widget session.");
                }
                lock (_reconnectGate)
                {
                    _readySignal.TrySetResult();
                }
                if (await RemainsReadyAsync(_stableReadyDuration, cancellationToken).ConfigureAwait(false))
                {
                    MarkTokenReady(token);
                    CompleteSuccessfulReconnect();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                return;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException or
                    InvalidOperationException or JsonException or WebSocketException)
            {
                if (IsRejectedWidgetToken(exception))
                {
                    SelectNextWidgetToken(token);
                }
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(
                delay.TotalMilliseconds * 2,
                MaximumReconnectDelay.TotalMilliseconds));
        }
    }

    private void CompleteSuccessfulReconnect()
    {
        lock (_reconnectGate)
        {
            // Publish the reconnect task as complete before it actually returns. If
            // ConnectionLost raced with stable-token persistence, the event handler
            // saw the still-running task and could not schedule a successor.
            _reconnectTask = null;
            if (!_disposed && !_socketClient.IsConnected)
            {
                ScheduleReconnectLocked();
            }
        }
    }

    private async Task<bool> RemainsReadyAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var stableUntil = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < stableUntil)
        {
            if (!_socketClient.IsConnected)
            {
                return false;
            }
            var remaining = stableUntil - DateTimeOffset.UtcNow;
            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(100)
                    ? remaining
                    : TimeSpan.FromMilliseconds(100),
                cancellationToken).ConfigureAwait(false);
        }
        return _socketClient.IsConnected;
    }

    private void MarkTokenReady(string token)
    {
        lock (_configurationGate)
        {
            if (token.Length == 0 ||
                !string.Equals(_widgetToken, token, StringComparison.Ordinal))
            {
                return;
            }
            _lastKnownGoodToken = token;
            _rejectedWidgetTokens.Remove(token);
        }
        TryStoreWidgetToken(token);
    }

    private bool SelectNextWidgetToken(string rejectedToken)
    {
        var clearStoredToken = false;
        WidgetTokenCandidate? next;
        lock (_configurationGate)
        {
            _rejectedWidgetTokens.Add(rejectedToken);
            if (string.Equals(_lastKnownGoodToken, rejectedToken, StringComparison.Ordinal))
            {
                _lastKnownGoodToken = string.Empty;
                clearStoredToken = true;
            }
            next = _widgetTokenCandidates.FirstOrDefault(candidate =>
                !_rejectedWidgetTokens.Contains(candidate.Token));
            if (next is not null)
            {
                _widgetToken = next.Token;
                _sourceName = next.SourceName;
            }
        }
        if (clearStoredToken)
        {
            TryClearStoredWidgetToken();
        }
        return next is not null;
    }

    private static bool IsRejectedWidgetToken(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } statusCode } &&
        (int)statusCode is >= 400 and < 500 &&
        statusCode is not System.Net.HttpStatusCode.RequestTimeout and
            not System.Net.HttpStatusCode.TooManyRequests;

    private long ReadAlertId(DonationAlert donation)
    {
        ArgumentNullException.ThrowIfNull(donation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!long.TryParse(donation.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var alertId) ||
            alertId <= 0)
        {
            throw new InvalidOperationException("DonationAlerts returned an invalid donation identifier.");
        }

        return alertId;
    }

    private string GetWidgetToken()
    {
        lock (_configurationGate)
        {
            if (_widgetToken.Length > 0)
            {
                return _widgetToken;
            }
        }
        if (!TryAutoConfigure())
        {
            throw new InvalidOperationException(
                "DonationAlerts Alerts widget was not found in the OBS scene collections.");
        }
        lock (_configurationGate)
        {
            return _widgetToken;
        }
    }

    private bool TryLoadStoredWidgetToken(out string token)
    {
        token = string.Empty;
        byte[]? stored = null;
        try
        {
            stored = _widgetTokenStore.Load();
            if (stored is null || stored.Length is < 10 or > 128)
            {
                return false;
            }

            var candidate = Encoding.UTF8.GetString(stored);
            if (!IsValidWidgetToken(candidate))
            {
                _widgetTokenStore.Clear();
                return false;
            }

            token = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or
                InvalidDataException)
        {
            return false;
        }
        finally
        {
            if (stored is not null)
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }
    }

    private void TryStoreWidgetToken(string token)
    {
        var data = Encoding.UTF8.GetBytes(token);
        try
        {
            _ = _widgetTokenStore.TrySave(data);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Direct control still works for this process even when secure persistence is unavailable.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private void TryClearStoredWidgetToken()
    {
        try
        {
            _widgetTokenStore.Clear();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A rejected token is also kept out of the in-memory candidate set for this
            // process, even when the secure store cannot be updated.
        }
    }

    private static void AddCandidate(
        ICollection<WidgetTokenCandidate> candidates,
        WidgetTokenCandidate candidate)
    {
        if (!candidates.Any(existing =>
                string.Equals(existing.Token, candidate.Token, StringComparison.Ordinal)))
        {
            candidates.Add(candidate);
        }
    }

    private static TaskCompletionSource CreateReadySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void CollectAlertsWidgets(
        JsonElement element,
        ICollection<WidgetTokenCandidate> candidates)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("settings", out var settings) &&
                settings.ValueKind == JsonValueKind.Object &&
                settings.TryGetProperty("url", out var urlValue) &&
                urlValue.ValueKind == JsonValueKind.String &&
                TryReadWidgetToken(urlValue.GetString(), out var token))
            {
                var sourceName = element.TryGetProperty("name", out var name) &&
                                 name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? string.Empty
                    : string.Empty;
                AddCandidate(candidates, new WidgetTokenCandidate(token, sourceName));
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectAlertsWidgets(property.Value, candidates);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectAlertsWidgets(item, candidates);
            }
        }
    }

    private static bool TryReadWidgetToken(string? value, out string token)
    {
        token = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, DonationAlertsHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), AlertsWidgetPath, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var query = uri.Query.AsSpan().TrimStart('?');
        foreach (var pair in query.ToString().Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || !string.Equals(
                    Uri.UnescapeDataString(pair[..separator]),
                    "token",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var candidate = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (IsValidWidgetToken(candidate))
            {
                token = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool IsValidWidgetToken(string candidate) =>
        candidate.Length is >= 10 and <= 128 && candidate.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ResolveObsScenesDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio",
        "basic",
        "scenes");

    private sealed record WidgetTokenCandidate(string Token, string SourceName);
}
