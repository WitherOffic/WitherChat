using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WitherChat.Core.Services;

internal sealed partial class DonationAlertsWidgetSocketClient :
    IDonationAlertsWidgetSocketClient,
    IDisposable
{
    private const int MaximumSocketMessageBytes = 1024 * 1024;
    private const int EngineIoVersion = 3;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartConfirmationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SkipConfirmationTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri WidgetRoot = new("https://www.donationalerts.com/widget/alerts");
    private static readonly Uri RepeatAlertRoot = new("https://www.donationalerts.com/api/repeatalert");
    private readonly HttpClient _httpClient;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _socketFactory;
    private readonly SemaphoreSlim _hostGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<long, TaskCompletionSource> _startWaiters = [];
    private readonly HashSet<long> _verifiedPayloadIds = [];
    private readonly HashSet<long> _unverifiedStarts = [];
    private WebSocket? _socket;
    private CancellationTokenSource? _socketCancellation;
    private Task? _receiveTask;
    private TaskCompletionSource? _skipWaiter;
    private long _skipAlertId;
    private string _connectedToken = string.Empty;
    private long _activeAlertId;
    private bool _isReady;
    private bool _disposed;

    public DonationAlertsWidgetSocketClient(HttpMessageHandler? handler = null)
        : this(handler, ConnectSocketAsync)
    {
    }

    internal DonationAlertsWidgetSocketClient(
        HttpMessageHandler? handler,
        Func<Uri, CancellationToken, Task<WebSocket>> socketFactory)
    {
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _httpClient = handler is null
            ? new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    CheckCertificateRevocationList = true,
                    UseCookies = true,
                    CookieContainer = new System.Net.CookieContainer()
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;

    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _isReady && _socket?.State == WebSocketState.Open;
            }
        }
    }

    public bool IsAlertPlaying
    {
        get
        {
            lock (_stateGate)
            {
                return _activeAlertId > 0;
            }
        }
    }

    public long ActiveAlertId
    {
        get
        {
            lock (_stateGate)
            {
                return _activeAlertId;
            }
        }
    }

    public async Task PrepareAsync(
        string widgetToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateGate)
        {
            if (_isReady && _socket?.State == WebSocketState.Open &&
                string.Equals(_connectedToken, widgetToken, StringComparison.Ordinal))
            {
                return;
            }
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_isReady && _socket?.State == WebSocketState.Open &&
                    string.Equals(_connectedToken, widgetToken, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await CloseConnectionAsync().ConfigureAwait(false);
            var socketHost = await ResolveSocketHostAsync(widgetToken, cancellationToken).ConfigureAwait(false);
            var endpoint = new UriBuilder(socketHost)
            {
                Scheme = Uri.UriSchemeWss,
                Port = socketHost.IsDefaultPort ? -1 : socketHost.Port,
                Path = "/socket.io/",
                Query = $"EIO={EngineIoVersion}&transport=websocket"
            }.Uri;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectionTimeout);
            WebSocket? socket = null;
            EngineHeartbeatSettings heartbeatSettings;
            IReadOnlyList<string> bufferedPackets;
            try
            {
                socket = await _socketFactory(endpoint, timeout.Token).ConfigureAwait(false) ??
                    throw new InvalidOperationException("DonationAlerts socket factory returned no socket.");
                heartbeatSettings = await WaitForEngineOpenAsync(socket, timeout.Token).ConfigureAwait(false);
                // DonationAlerts currently advertises Engine.IO v3. In that protocol the
                // server initiates the default Socket.IO namespace connection. Sending a
                // client-side `40` here is an Engine.IO v4 behaviour and can make the DA
                // server close an otherwise healthy widget connection.
                if (ShouldSendNamespaceConnectPacket(EngineIoVersion))
                {
                    await SendTextDirectAsync(socket, "40", timeout.Token).ConfigureAwait(false);
                }
                await WaitForSocketConnectionAsync(socket, timeout.Token).ConfigureAwait(false);
                await SendEventDirectAsync(
                    socket,
                    "add-user",
                    new { token = widgetToken, type = "minor" },
                    timeout.Token).ConfigureAwait(false);
                // A Socket.IO `40` only proves that the namespace transport opened. The
                // widget token was validated by ResolveSocketHostAsync above, and this
                // post-auth Engine.IO v3 pong proves that the DA socket remains usable
                // after add-user. Only this combination is exposed as Ready.
                bufferedPackets = await WaitForWidgetReadyAsync(socket, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                socket?.Dispose();
                throw;
            }

            var runCancellation = new CancellationTokenSource();
            var startReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateGate)
            {
                _socket = socket;
                _connectedToken = widgetToken;
                _socketCancellation = runCancellation;
                _activeAlertId = 0;
                _isReady = true;
                _receiveTask = ReceiveLoopAsync(
                    socket,
                    heartbeatSettings,
                    startReceive.Task,
                    runCancellation.Token);
            }
            try
            {
                PlaybackChanged?.Invoke(
                    this,
                    new DonationAlertsPlaybackEventArgs(
                        DonationAlertsPlaybackAction.ConnectionRestored,
                        0));
                foreach (var packet in bufferedPackets)
                {
                    ProcessSocketPacket(packet);
                }
            }
            finally
            {
                startReceive.TrySetResult();
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task StartAlertAsync(
        string widgetToken,
        long alertId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await PrepareAsync(widgetToken, cancellationToken).ConfigureAwait(false);

        TaskCompletionSource waiter;
        var sendCommand = false;
        lock (_stateGate)
        {
            if (_activeAlertId == alertId)
            {
                throw new InvalidOperationException(
                    "DonationAlerts is already playing this alert.");
            }
            if (_socket is not { State: WebSocketState.Open })
            {
                throw new InvalidOperationException("DonationAlerts direct control is not connected.");
            }
            if (!_startWaiters.TryGetValue(alertId, out waiter!))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _startWaiters.Add(alertId, waiter);
                sendCommand = true;
            }
        }

        if (sendCommand)
        {
            try
            {
                await SendRepeatRequestAsync(
                    widgetToken,
                    alertId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                waiter.TrySetException(exception);
                lock (_stateGate)
                {
                    if (_startWaiters.TryGetValue(alertId, out var registered) &&
                        ReferenceEquals(registered, waiter))
                    {
                        _startWaiters.Remove(alertId);
                    }
                }
                throw;
            }
        }

        await WaitForStartCoreAsync(alertId, waiter, cancellationToken).ConfigureAwait(false);
    }

    public async Task SkipAlertAsync(
        string widgetToken,
        long alertId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await PrepareAsync(widgetToken, cancellationToken).ConfigureAwait(false);

        TaskCompletionSource waiter;
        WebSocket socket;
        long activeAlertId;
        var sendCommand = false;
        lock (_stateGate)
        {
            activeAlertId = _activeAlertId;
            if (activeAlertId <= 0)
            {
                throw new InvalidOperationException(
                    "DonationAlerts has not confirmed an actively playing alert.");
            }
            if (activeAlertId != alertId)
            {
                throw new InvalidOperationException(
                    "DonationAlerts is playing a different alert than the requested donation.");
            }
            socket = _socket ?? throw new InvalidOperationException(
                "DonationAlerts direct control is not connected.");
            if (_skipWaiter is null)
            {
                _skipWaiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _skipAlertId = alertId;
                sendCommand = true;
            }
            else if (_skipAlertId != alertId)
            {
                throw new InvalidOperationException(
                    "DonationAlerts is already processing a skip command for another alert.");
            }
            waiter = _skipWaiter;
        }

        if (sendCommand)
        {
            try
            {
                await SendEventAsync(
                    socket,
                    "alert-show",
                    new
                    {
                        token = widgetToken,
                        message_data = new
                        {
                            action = "skip",
                            alert_id = activeAlertId,
                            alert_type = 1
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                waiter.TrySetException(exception);
                lock (_stateGate)
                {
                    if (ReferenceEquals(_skipWaiter, waiter))
                    {
                        _skipWaiter = null;
                        _skipAlertId = 0;
                    }
                }
                throw;
            }
        }

        try
        {
            await waiter.Task.WaitAsync(SkipConfirmationTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseSkipWaiter(waiter, sendCommand);
        }
    }

    private void ReleaseSkipWaiter(TaskCompletionSource waiter, bool commandOwner)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_skipWaiter, waiter) &&
                (waiter.Task.IsCompleted || commandOwner))
            {
                _skipWaiter = null;
                _skipAlertId = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _socketCancellation?.Cancel();
        _socket?.Dispose();
        FailPendingCommands(new ObjectDisposedException(nameof(DonationAlertsWidgetSocketClient)));
        _hostGate.Dispose();
        _connectionGate.Dispose();
        _sendGate.Dispose();
        _socketCancellation?.Dispose();
        _httpClient.Dispose();
    }

    internal static bool TryReadSocketHost(string html, out Uri socketHost)
    {
        socketHost = null!;
        var match = SocketHostPattern().Match(html);
        if (!match.Success ||
            !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeWss ||
            candidate.Port != 443 ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !SocketDomainPattern().IsMatch(candidate.Host) ||
            candidate.AbsolutePath != "/")
        {
            return false;
        }

        socketHost = candidate;
        return true;
    }

    private async Task WaitForStartCoreAsync(
        long alertId,
        TaskCompletionSource waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            await waiter.Task.WaitAsync(StartConfirmationTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
            {
                if (_startWaiters.TryGetValue(alertId, out var registered) &&
                    ReferenceEquals(registered, waiter))
                {
                    _startWaiters.Remove(alertId);
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket socket,
        EngineHeartbeatSettings heartbeatSettings,
        Task startReceive,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        await startReceive.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatState = new EngineHeartbeatState();
        var heartbeatTask = RunEngineHeartbeatAsync(
            socket,
            heartbeatSettings,
            heartbeatState,
            heartbeatCancellation.Token);
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var packet = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
                if (packet.StartsWith('3'))
                {
                    heartbeatState.CompletePong();
                    continue;
                }
                if (packet.StartsWith('2'))
                {
                    await SendTextAsync(socket, "3" + packet[1..], cancellationToken).ConfigureAwait(false);
                    continue;
                }
                ProcessSocketPacket(packet);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is WebSocketException or IOException or InvalidDataException or JsonException or
                ObjectDisposedException)
        {
            failure = exception;
        }
        finally
        {
            await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (
                exception is WebSocketException or IOException or ObjectDisposedException)
            {
                failure ??= exception;
            }
            var notifyLost = false;
            lock (_stateGate)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _connectedToken = string.Empty;
                    _activeAlertId = 0;
                    notifyLost = _isReady && !_disposed;
                    _isReady = false;
                }
            }
            if (notifyLost)
            {
                failure ??= new IOException("DonationAlerts closed the direct control connection.");
            }
            if (failure is not null)
            {
                FailPendingCommands(failure);
            }
            if (notifyLost)
            {
                PlaybackChanged?.Invoke(
                    this,
                    new DonationAlertsPlaybackEventArgs(
                        DonationAlertsPlaybackAction.ConnectionLost,
                        0));
            }
        }
    }

    private async Task RunEngineHeartbeatAsync(
        WebSocket socket,
        EngineHeartbeatSettings settings,
        EngineHeartbeatState state,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(settings.PingInterval, cancellationToken).ConfigureAwait(false);
            var pong = state.BeginPing();
            await SendTextAsync(socket, "2", cancellationToken).ConfigureAwait(false);
            try
            {
                await pong.Task.WaitAsync(settings.PingTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                socket.Abort();
                throw new IOException("DonationAlerts heartbeat timed out.", exception);
            }
        }
    }

    internal void ProcessSocketPacket(string packet)
    {
        if (TryReadAlertAction(packet, out var action, out var alertId))
        {
            ProcessPlaybackAction(action, alertId);
        }
        else if (TryReadDonationPayloadId(packet, out var donationId))
        {
            ProcessDonationPayload(donationId);
        }
    }

    private void ProcessPlaybackAction(string action, long alertId)
    {
        DonationAlertsPlaybackAction? playbackAction = action switch
        {
            "start" => DonationAlertsPlaybackAction.Started,
            "end" => DonationAlertsPlaybackAction.Ended,
            "skip" => DonationAlertsPlaybackAction.Skipped,
            _ => null
        };
        if (playbackAction is null)
        {
            return;
        }

        lock (_stateGate)
        {
            if (playbackAction == DonationAlertsPlaybackAction.Started)
            {
                if (!_verifiedPayloadIds.Remove(alertId))
                {
                    _unverifiedStarts.Add(alertId);
                    return;
                }
                _activeAlertId = alertId;
                if (_startWaiters.TryGetValue(alertId, out var startWaiter))
                {
                    startWaiter.TrySetResult();
                }
            }
            else
            {
                _unverifiedStarts.Remove(alertId);
                if (_activeAlertId == alertId)
                {
                    _activeAlertId = 0;
                    if (_skipAlertId == alertId)
                    {
                        _skipWaiter?.TrySetResult();
                    }
                }
            }
        }
        PlaybackChanged?.Invoke(this, new DonationAlertsPlaybackEventArgs(playbackAction.Value, alertId));
    }

    private void ProcessDonationPayload(long alertId)
    {
        var completeEarlyStart = false;
        lock (_stateGate)
        {
            if (_activeAlertId == alertId)
            {
                return;
            }
            if (!_verifiedPayloadIds.Contains(alertId))
            {
                if (_verifiedPayloadIds.Count >= 512)
                {
                    _verifiedPayloadIds.Clear();
                }
                _verifiedPayloadIds.Add(alertId);
            }
            completeEarlyStart = _unverifiedStarts.Remove(alertId);
        }
        if (completeEarlyStart)
        {
            ProcessPlaybackAction("start", alertId);
        }
    }

    private void FailPendingCommands(Exception exception)
    {
        lock (_stateGate)
        {
            foreach (var waiter in _startWaiters.Values)
            {
                waiter.TrySetException(exception);
            }
            _startWaiters.Clear();
            _skipWaiter?.TrySetException(exception);
            _skipWaiter = null;
            _skipAlertId = 0;
            _unverifiedStarts.Clear();
        }
    }

    private async Task CloseConnectionAsync()
    {
        WebSocket? socket;
        CancellationTokenSource? cancellation;
        Task? receiveTask;
        var hadConnection = false;
        lock (_stateGate)
        {
            socket = _socket;
            cancellation = _socketCancellation;
            receiveTask = _receiveTask;
            hadConnection = socket is not null || receiveTask is not null;
            _socket = null;
            _socketCancellation = null;
            _receiveTask = null;
            _connectedToken = string.Empty;
            _activeAlertId = 0;
            _isReady = false;
            _verifiedPayloadIds.Clear();
            _unverifiedStarts.Clear();
        }
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        socket?.Dispose();
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or WebSocketException)
            {
            }
        }
        cancellation?.Dispose();
        if (hadConnection)
        {
            FailPendingCommands(new IOException(
                "DonationAlerts direct control connection was replaced."));
        }
    }

    private async Task<Uri> ResolveSocketHostAsync(
        string widgetToken,
        CancellationToken cancellationToken)
    {
        await _hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var uri = new UriBuilder(WidgetRoot)
            {
                Query = "token=" + Uri.EscapeDataString(widgetToken)
            }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"DonationAlerts widget returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!TryReadSocketHost(html, out var socketHost))
            {
                throw new InvalidDataException(
                    "DonationAlerts returned an invalid or untrusted widget socket address.");
            }

            return socketHost;
        }
        finally
        {
            _hostGate.Release();
        }
    }

    private async Task SendRepeatRequestAsync(
        string widgetToken,
        long alertId,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendRepeatRequestCoreAsync(widgetToken, alertId, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // A widget session can expire while the persistent socket stays open.
            // A confirmed 403 means the repeat was not accepted, so one cookie refresh
            // and one retry cannot duplicate an alert.
            await RefreshWidgetSessionAsync(widgetToken, cancellationToken).ConfigureAwait(false);
            await SendRepeatRequestCoreAsync(widgetToken, alertId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendRepeatRequestCoreAsync(
        string widgetToken,
        long alertId,
        CancellationToken cancellationToken)
    {
        var requestUri = new UriBuilder(RepeatAlertRoot)
        {
            Query = "alert=" + alertId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    "&alert_type=1&token=" + Uri.EscapeDataString(widgetToken)
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Referrer = new UriBuilder(WidgetRoot)
        {
            Query = "token=" + Uri.EscapeDataString(widgetToken)
        }.Uri;
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"DonationAlerts repeat returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }
        ValidateRepeatResponse(body);
    }

    private async Task RefreshWidgetSessionAsync(
        string widgetToken,
        CancellationToken cancellationToken)
    {
        await _hostGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var uri = new UriBuilder(WidgetRoot)
            {
                Query = "token=" + Uri.EscapeDataString(widgetToken)
            }.Uri;
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"DonationAlerts widget returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }
        }
        finally
        {
            _hostGate.Release();
        }
    }

    internal static void ValidateRepeatResponse(string body)
    {
        var normalized = body.Trim();
        if (normalized.StartsWith('(') && normalized.EndsWith(')') && normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.TryGetProperty("status", out var status) &&
                string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                var message = document.RootElement.TryGetProperty("message", out var detail)
                    ? detail.GetString()
                    : null;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                    ? "DonationAlerts rejected the repeat command."
                    : message);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "DonationAlerts returned an invalid repeat response.",
                exception);
        }
    }

    private async Task SendEventAsync(
        WebSocket socket,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendEventDirectAsync(socket, eventName, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task SendTextAsync(
        WebSocket socket,
        string value,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendTextDirectAsync(socket, value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static async Task<EngineHeartbeatSettings> WaitForEngineOpenAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (packet.StartsWith('0'))
            {
                if (TryReadEngineHeartbeatSettings(packet, out var settings))
                {
                    return settings;
                }
                throw new InvalidDataException("DonationAlerts returned invalid heartbeat settings.");
            }
            if (packet.StartsWith('2'))
            {
                await SendTextDirectAsync(socket, "3" + packet[1..], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool TryReadEngineHeartbeatSettings(
        string packet,
        out EngineHeartbeatSettings settings)
    {
        settings = default;
        if (string.IsNullOrWhiteSpace(packet) || !packet.StartsWith('0'))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(packet[1..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("pingInterval", out var interval) ||
                !interval.TryGetInt32(out var intervalMilliseconds) ||
                !root.TryGetProperty("pingTimeout", out var timeout) ||
                !timeout.TryGetInt32(out var timeoutMilliseconds) ||
                intervalMilliseconds is < 1_000 or > 120_000 ||
                timeoutMilliseconds is < 1_000 or > 120_000)
            {
                return false;
            }
            settings = new EngineHeartbeatSettings(
                TimeSpan.FromMilliseconds(intervalMilliseconds),
                TimeSpan.FromMilliseconds(timeoutMilliseconds));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool ShouldSendNamespaceConnectPacket(int engineIoVersion) => engineIoVersion >= 4;

    internal static bool IsWidgetReadyFrame(string packet, bool widgetTokenValidated) =>
        widgetTokenValidated && !string.IsNullOrEmpty(packet) && packet.StartsWith('3');

    private static async Task<IReadOnlyList<string>> WaitForWidgetReadyAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        // Engine.IO v3 heartbeats are client ping -> server pong. Sending one
        // immediately avoids publishing a connection that dies on its first regular
        // heartbeat (the source of the former connect/disconnect flapping).
        await SendTextDirectAsync(socket, "2", cancellationToken).ConfigureAwait(false);
        var bufferedPackets = new List<string>();
        while (true)
        {
            var packet = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (IsWidgetReadyFrame(packet, widgetTokenValidated: true))
            {
                return bufferedPackets;
            }
            if (packet.StartsWith('2'))
            {
                await SendTextDirectAsync(socket, "3" + packet[1..], cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            if (packet.StartsWith("42[", StringComparison.Ordinal))
            {
                if (bufferedPackets.Count >= 64)
                {
                    throw new InvalidDataException(
                        "DonationAlerts sent too many widget events before confirming readiness.");
                }
                bufferedPackets.Add(packet);
                continue;
            }
            if (packet == "41" || packet.StartsWith("44", StringComparison.Ordinal))
            {
                throw new IOException("DonationAlerts rejected the widget socket session.");
            }
            // A repeated namespace-connect packet is transport state, not readiness.
        }
    }

    private static async Task WaitForSocketConnectionAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (packet == "40")
            {
                return;
            }
            if (packet.StartsWith('2'))
            {
                await SendTextDirectAsync(socket, "3" + packet[1..], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool TryReadAlertAction(
        string packet,
        out string action,
        out long alertId)
    {
        action = string.Empty;
        alertId = 0;
        if (!packet.StartsWith("42[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(packet[2..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 ||
                !string.Equals(root[0].GetString(), "alert-show", StringComparison.Ordinal))
            {
                return false;
            }

            var payload = root[1];
            if (payload.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(payload.GetString() ?? string.Empty);
                return TryReadAlertActionPayload(nested.RootElement, out action, out alertId);
            }

            return TryReadAlertActionPayload(payload, out action, out alertId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadDonationPayloadId(string packet, out long alertId)
    {
        alertId = 0;
        if (!packet.StartsWith("42[", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(packet[2..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 ||
                !string.Equals(root[0].GetString(), "donation", StringComparison.Ordinal))
            {
                return false;
            }
            var payload = root[1];
            if (payload.ValueKind == JsonValueKind.String)
            {
                using var nested = JsonDocument.Parse(payload.GetString() ?? string.Empty);
                return TryReadDonationId(nested.RootElement, out alertId);
            }
            return TryReadDonationId(payload, out alertId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDonationId(JsonElement payload, out long alertId)
    {
        alertId = 0;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (!payload.TryGetProperty("id", out var idElement) &&
            payload.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object)
        {
            _ = data.TryGetProperty("id", out idElement);
        }
        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.TryGetInt64(out alertId) && alertId > 0,
            JsonValueKind.String => long.TryParse(idElement.GetString(), out alertId) && alertId > 0,
            _ => false
        };
    }

    private static bool TryReadAlertActionPayload(
        JsonElement payload,
        out string action,
        out long alertId)
    {
        action = string.Empty;
        alertId = 0;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("action", out var actionElement) ||
            !payload.TryGetProperty("alert_id", out var idElement))
        {
            return false;
        }

        action = actionElement.GetString() ?? string.Empty;
        return idElement.ValueKind switch
        {
            JsonValueKind.Number => idElement.TryGetInt64(out alertId),
            JsonValueKind.String => long.TryParse(idElement.GetString(), out alertId),
            _ => false
        };
    }

    private static Task SendEventDirectAsync(
        WebSocket socket,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var packet = "42" + JsonSerializer.Serialize(new object[] { eventName, payload });
        return SendTextDirectAsync(socket, packet, cancellationToken);
    }

    private static Task SendTextDirectAsync(
        WebSocket socket,
        string value,
        CancellationToken cancellationToken)
    {
        var buffer = Encoding.UTF8.GetBytes(value);
        return socket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task<WebSocket> ConnectSocketAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string> ReceiveTextAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(rented),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("DonationAlerts closed the direct control connection.");
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }
                if (stream.Length + result.Count > MaximumSocketMessageBytes)
                {
                    throw new InvalidDataException("DonationAlerts widget socket message is too large.");
                }
                await stream.WriteAsync(
                    rented.AsMemory(0, result.Count),
                    cancellationToken).ConfigureAwait(false);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal readonly record struct EngineHeartbeatSettings(
        TimeSpan PingInterval,
        TimeSpan PingTimeout);

    private sealed class EngineHeartbeatState
    {
        private readonly object _gate = new();
        private TaskCompletionSource _pong = CompletedPong();

        public TaskCompletionSource BeginPing()
        {
            lock (_gate)
            {
                _pong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pong;
            }
        }

        public void CompletePong()
        {
            lock (_gate)
            {
                _pong.TrySetResult();
            }
        }

        private static TaskCompletionSource CompletedPong()
        {
            var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            value.TrySetResult();
            return value;
        }
    }

    [GeneratedRegex("SocketIOClientProxy\\(\\{\\s*socketHost:\\s*['\\\"]([^'\\\"]+)['\\\"]", RegexOptions.CultureInvariant)]
    private static partial Regex SocketHostPattern();

    [GeneratedRegex("^socket[0-9]+\\.donationalerts\\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SocketDomainPattern();
}
