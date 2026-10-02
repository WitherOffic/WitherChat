using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class DonationAlertsClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private const int MaximumHistoryPages = 1000;
    private const int MaximumRememberedDonationIds = 10_000;
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StableConnectionDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SocketReceiveTimeout = TimeSpan.FromMinutes(2);
    private static readonly Uri UserEndpoint = new("https://www.donationalerts.com/api/v1/user/oauth");
    private static readonly Uri DonationsEndpoint = new("https://www.donationalerts.com/api/v1/alerts/donations");
    private static readonly Uri SubscribeEndpoint = new("https://www.donationalerts.com/api/v1/centrifuge/subscribe");
    private static readonly Uri WebSocketEndpoint = new("wss://centrifugo.donationalerts.com/connection/websocket");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Queue<string> _recentDonationIds = new();
    private readonly HashSet<string> _recentDonationIdSet = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _disposed;

    public DonationAlertsClient(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null
            ? new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    CheckCertificateRevocationList = true
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
    }

    public event EventHandler<DonationAlertEventArgs>? DonationReceived;
    public event EventHandler<DonationAlertsConnectionStatusEventArgs>? StatusChanged;

    public async Task<IReadOnlyList<DonationAlert>> GetDonationHistoryAsync(
        DonationAlertsAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!DonationAlertsApplication.HasRequiredScopes(session.Scopes))
        {
            throw new InvalidOperationException(
                "DonationAlerts history permission is missing. Reconnect the account.");
        }

        var donations = new List<DonationAlert>();
        var donationIds = new HashSet<string>(StringComparer.Ordinal);
        var visitedPages = new HashSet<string>(StringComparer.Ordinal);
        Uri? pageUri = DonationsEndpoint;
        while (pageUri is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visitedPages.Count >= MaximumHistoryPages || !visitedPages.Add(pageUri.AbsoluteUri))
            {
                throw new InvalidDataException("DonationAlerts returned an invalid pagination sequence.");
            }

            using var request = CreateAuthorizedRequest(HttpMethod.Get, pageUri, session.AccessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await ReadSuccessBodyAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("DonationAlerts returned an invalid donation history.");
            }

            foreach (var item in data.EnumerateArray())
            {
                if (TryParseDonation(item.GetRawText(), out var donation) &&
                    donation is not null && donationIds.Add(donation.Id))
                {
                    donations.Add(donation);
                }
            }
            pageUri = ReadNextDonationsPageUri(document.RootElement);
        }

        return donations
            .OrderByDescending(donation => donation.ReceivedAtUtc)
            .ThenByDescending(donation => donation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task StartAsync(
        DonationAlertsAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopCoreAsync(raiseStatus: false).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = cancellation;
            _runTask = RunReconnectLoopAsync(session, cancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(raiseStatus: true).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(bool raiseStatus)
    {
        var cancellation = _runCancellation;
        var runTask = _runTask;
        _runCancellation = null;
        _runTask = null;
        if (cancellation is null)
        {
            return;
        }
        await cancellation.CancelAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation.Dispose();
        if (raiseStatus)
        {
            RaiseStatus(false, false, "Disconnected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            await StopCoreAsync(raiseStatus: false).ConfigureAwait(false);
            _httpClient.Dispose();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal static bool TryParseDonation(string json, out DonationAlert? donation)
    {
        donation = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumMessageBytes)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryFindDonationObject(document.RootElement, out var element))
            {
                return false;
            }

            var id = ReadString(element, "id");
            var username = ReadString(element, "username");
            var currency = ReadString(element, "currency");
            if (id.Length == 0 || currency.Length == 0 || !TryReadAmount(element, out var amount))
            {
                return false;
            }
            donation = new DonationAlert(
                id,
                Limit(username, 128),
                Limit(ReadString(element, "message"), 4000),
                amount,
                Limit(currency.ToUpperInvariant(), 12),
                ReadTimestamp(element, "created_at") ?? DateTimeOffset.UtcNow,
                ReadBooleanFlag(element, "is_shown"),
                ReadTimestamp(element, "shown_at"),
                Limit(ReadString(element, "message_type"), 16) is { Length: > 0 } messageType
                    ? messageType
                    : "text");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task RunReconnectLoopAsync(
        DonationAlertsAuthSession session,
        CancellationToken cancellationToken)
    {
        var reconnectDelay = InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset? connectedAt = null;
            try
            {
                RaiseStatus(false, true, "Connecting");
                await ConnectAndReceiveAsync(
                    session,
                    () => connectedAt = DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                IsTransientConnectionException(exception))
            {
                RaiseStatus(false, true, exception.Message);
            }

            var connectionWasStable = connectedAt is { } startedAt &&
                                      DateTimeOffset.UtcNow - startedAt >= StableConnectionDuration;
            var delayBeforeRetry = connectionWasStable ? InitialReconnectDelay : reconnectDelay;
            try
            {
                await Task.Delay(delayBeforeRetry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            reconnectDelay = AdvanceReconnectDelay(delayBeforeRetry, connectionWasStable);
        }
    }

    private async Task ConnectAndReceiveAsync(
        DonationAlertsAuthSession session,
        Action onConnected,
        CancellationToken cancellationToken)
    {
        var socketToken = await GetSocketTokenAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
        using var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCancellation.CancelAfter(SocketConnectTimeout);
            try
            {
                await webSocket.ConnectAsync(WebSocketEndpoint, connectCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("DonationAlerts real-time connection timed out.", exception);
            }
        }

        await SendJsonAsync(
            webSocket,
            new { @params = new { token = socketToken }, id = 1 },
            cancellationToken).ConfigureAwait(false);
        var clientId = string.Empty;
        while (clientId.Length == 0)
        {
            var handshake = await ReadMessageWithTimeoutAsync(webSocket, cancellationToken)
                .ConfigureAwait(false);
            foreach (var message in SplitProtocolMessages(handshake))
            {
                if (message == "{}")
                {
                    await SendTextAsync(webSocket, "{}", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                clientId = ReadHandshakeClientId(message);
                if (clientId.Length > 0)
                {
                    break;
                }
            }
        }

        var channel = "$alerts:donation_" + session.UserId.ToString(CultureInfo.InvariantCulture);
        var subscriptionToken = await GetSubscriptionTokenAsync(
            session.AccessToken,
            clientId,
            channel,
            cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(
            webSocket,
            new { @params = new { channel, token = subscriptionToken }, method = 1, id = 2 },
            cancellationToken).ConfigureAwait(false);
        var subscriptionConfirmed = false;
        while (!subscriptionConfirmed)
        {
            var subscriptionResult = await ReadMessageWithTimeoutAsync(webSocket, cancellationToken)
                .ConfigureAwait(false);
            foreach (var message in SplitProtocolMessages(subscriptionResult))
            {
                if (message == "{}")
                {
                    await SendTextAsync(webSocket, "{}", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (IsValidSubscriptionAcknowledgement(message, channel))
                {
                    subscriptionConfirmed = true;
                    continue;
                }
                ProcessIncomingPayload(message);
            }
        }
        RaiseStatus(true, false, "Connected");
        onConnected();

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var payload = await ReadMessageWithTimeoutAsync(webSocket, cancellationToken)
                .ConfigureAwait(false);
            foreach (var message in SplitProtocolMessages(payload))
            {
                if (message == "{}")
                {
                    await SendTextAsync(webSocket, "{}", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                ProcessIncomingPayload(message);
            }
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("DonationAlerts closed the real-time connection.");
        }
    }

    private async Task<string> GetSocketTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, UserEndpoint, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadSuccessBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("socket_connection_token", out var token) &&
            token.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(token.GetString()))
        {
            return token.GetString()!;
        }
        throw new InvalidDataException("DonationAlerts returned no WebSocket connection token.");
    }

    private async Task<string> GetSubscriptionTokenAsync(
        string accessToken,
        string clientId,
        string channel,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Post, SubscribeEndpoint, accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { channels = new[] { channel }, client = clientId }, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadSuccessBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("channels", out var channels) &&
            channels.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in channels.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadString(item, "channel"), channel, StringComparison.Ordinal) &&
                    item.TryGetProperty("token", out var token) &&
                    token.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(token.GetString()))
                {
                    return token.GetString()!;
                }
            }
        }
        throw new InvalidDataException("DonationAlerts returned no private channel token.");
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        Uri endpoint,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<string> ReadSuccessBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"DonationAlerts API returned {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }
        return body;
    }

    private void ProcessIncomingPayload(string payload)
    {
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseDonation(line, out var donation) || donation is null || !RememberDonation(donation.Id))
            {
                continue;
            }
            DonationReceived?.Invoke(this, new DonationAlertEventArgs(donation));
        }
    }

    private bool RememberDonation(string id)
    {
        lock (_recentDonationIds)
        {
            if (!_recentDonationIdSet.Add(id))
            {
                return false;
            }
            _recentDonationIds.Enqueue(id);
            while (_recentDonationIds.Count > MaximumRememberedDonationIds)
            {
                _ = _recentDonationIdSet.Remove(_recentDonationIds.Dequeue());
            }
            return true;
        }
    }

    internal static string ReadHandshakeClientId(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }
        var isHandshakeResponse = root.TryGetProperty("id", out var id) &&
                                  id.ValueKind == JsonValueKind.Number &&
                                  id.TryGetInt32(out var messageId) &&
                                  messageId == 1;
        if (isHandshakeResponse && root.TryGetProperty("error", out var error))
        {
            throw new InvalidDataException(
                "DonationAlerts rejected the real-time handshake: " + error.GetRawText());
        }
        if (root.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("client", out var client) &&
            client.ValueKind == JsonValueKind.String)
        {
            return client.GetString() ?? string.Empty;
        }
        if (isHandshakeResponse)
        {
            throw new InvalidDataException(
                "DonationAlerts did not return a Centrifugo client ID.");
        }
        return string.Empty;
    }

    internal static IReadOnlyList<string> SplitProtocolMessages(string payload) =>
        payload.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static TimeSpan AdvanceReconnectDelay(
        TimeSpan currentDelay,
        bool connectionWasStable)
    {
        if (currentDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(currentDelay));
        }
        if (connectionWasStable)
        {
            return InitialReconnectDelay;
        }
        return TimeSpan.FromMilliseconds(Math.Min(
            MaximumReconnectDelay.TotalMilliseconds,
            currentDelay.TotalMilliseconds * 2));
    }

    internal static bool IsTransientConnectionException(Exception exception) =>
        exception is HttpRequestException or WebSocketException or IOException or InvalidDataException or
            InvalidOperationException or JsonException or OperationCanceledException or TimeoutException;

    internal static bool IsValidSubscriptionAcknowledgement(string json, string expectedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("DonationAlerts returned an invalid subscription response.");
        }

        var isSubscribeResponse = false;
        if (root.TryGetProperty("id", out var id))
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var messageId))
            {
                throw new InvalidDataException("DonationAlerts returned an invalid subscription response ID.");
            }
            if (messageId != 2)
            {
                return false;
            }
            isSubscribeResponse = true;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var detail = error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : error.GetRawText();
            throw new InvalidDataException(
                "DonationAlerts rejected the real-time subscription: " + detail);
        }

        if (!root.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object)
        {
            if (isSubscribeResponse)
            {
                throw new InvalidDataException("DonationAlerts did not confirm the real-time subscription.");
            }
            return false;
        }

        var hasType = result.TryGetProperty("type", out var type);
        var isConfirmation = hasType &&
                             type.ValueKind == JsonValueKind.Number &&
                             type.TryGetInt32(out var subscriptionType) &&
                             subscriptionType == 1;
        if (hasType && !isConfirmation)
        {
            throw new InvalidDataException("DonationAlerts returned an invalid subscription confirmation.");
        }
        if (!isSubscribeResponse && !isConfirmation)
        {
            return false;
        }
        if (result.TryGetProperty("channel", out var channel) &&
            (channel.ValueKind != JsonValueKind.String ||
             !string.Equals(channel.GetString(), expectedChannel, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("DonationAlerts confirmed an unexpected real-time channel.");
        }
        if (!isSubscribeResponse && !result.TryGetProperty("channel", out _))
        {
            throw new InvalidDataException("DonationAlerts did not identify the confirmed real-time channel.");
        }
        return true;
    }

    private static async Task SendJsonAsync(
        ClientWebSocket webSocket,
        object value,
        CancellationToken cancellationToken) =>
        await SendTextAsync(
            webSocket,
            JsonSerializer.Serialize(value, JsonOptions),
            cancellationToken).ConfigureAwait(false);

    private static async Task SendTextAsync(
        ClientWebSocket webSocket,
        string value,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await webSocket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadMessageAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await webSocket.ReceiveAsync(rented, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("DonationAlerts closed the WebSocket connection.");
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    if (result.EndOfMessage)
                    {
                        continue;
                    }
                    continue;
                }
                await stream.WriteAsync(rented.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumMessageBytes)
                {
                    throw new InvalidDataException("DonationAlerts WebSocket message is too large.");
                }
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task<string> ReadMessageWithTimeoutAsync(
        ClientWebSocket webSocket,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(SocketReceiveTimeout);
        try
        {
            return await ReadMessageAsync(webSocket, timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("DonationAlerts real-time connection became unresponsive.", exception);
        }
    }

    private static bool TryFindDonationObject(JsonElement element, out JsonElement donation)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasAmount = element.TryGetProperty("amount", out _);
            var hasCurrency = element.TryGetProperty("currency", out _);
            var hasId = element.TryGetProperty("id", out _);
            var isDonation = string.Equals(ReadString(element, "name"), "donation", StringComparison.OrdinalIgnoreCase);
            if (hasAmount && hasCurrency && hasId && (isDonation || element.TryGetProperty("username", out _)))
            {
                donation = element;
                return true;
            }
            foreach (var property in element.EnumerateObject())
            {
                if (TryFindDonationObject(property.Value, out donation))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindDonationObject(item, out donation))
                {
                    return true;
                }
            }
        }
        donation = default;
        return false;
    }

    private static bool TryReadAmount(JsonElement element, out decimal amount)
    {
        amount = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("amount", out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out amount),
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out amount),
            _ => false
        };
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool ReadBooleanFlag(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (value.Length == 0)
        {
            return null;
        }
        var styles = DateTimeStyles.AllowWhiteSpaces |
                     DateTimeStyles.AssumeUniversal |
                     DateTimeStyles.AdjustToUniversal;
        string[] exactFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH.mm.ss"];
        if (DateTimeOffset.TryParseExact(
                value,
                exactFormats,
                CultureInfo.InvariantCulture,
                styles,
                out var exactTimestamp))
        {
            return exactTimestamp;
        }
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles, out var timestamp)
            ? timestamp
            : null;
    }

    private static Uri? ReadNextDonationsPageUri(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("links", out var links) ||
            links.ValueKind != JsonValueKind.Object ||
            !links.TryGetProperty("next", out var next) ||
            next.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (next.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(next.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, DonationsEndpoint.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, DonationsEndpoint.AbsolutePath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("DonationAlerts returned an untrusted pagination URL.");
        }
        return uri;
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private void RaiseStatus(bool isConnected, bool isReconnecting, string detail) =>
        StatusChanged?.Invoke(
            this,
            new DonationAlertsConnectionStatusEventArgs(isConnected, isReconnecting, detail));
}
