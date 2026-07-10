using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class TwitchEventSubClient : IAsyncDisposable
{
    private const int MaxEventPayloadBytes = 1024 * 1024;
    private const string WebSocketUrl = "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30";
    private readonly TwitchApiClient _apiClient;
    private readonly FileLogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private ClientWebSocket? _socket;
    private string _broadcasterId = string.Empty;
    private string _chattingUserId = string.Empty;
    private TaskCompletionSource<bool>? _initialConnection;
    private bool _connectedReported;

    public event EventHandler<ChatMessageModel>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    public TwitchEventSubClient(TwitchApiClient apiClient, FileLogger logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task StartAsync(string broadcasterId, string chattingUserId, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        _broadcasterId = broadcasterId;
        _chattingUserId = chattingUserId;
        _connectedReported = false;
        _initialConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _logger.Info($"Starting chat connect: broadcaster_id={broadcasterId}, user_id={chattingUserId}");
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunLoopAsync(_runCts.Token), CancellationToken.None);
    }

    public async Task<bool> WaitForInitialConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var task = _initialConnection?.Task;
        if (task is null)
        {
            return false;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        ClientWebSocket? socket;

        lock (_gate)
        {
            cts = _runCts;
            task = _runTask;
            socket = _socket;
            _runCts = null;
            _runTask = null;
            _socket = null;
        }

        cts?.Cancel();
        if (socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", closeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Abort();
            }
        }

        if (task is not null)
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            }
            catch
            {
                // Stop is best-effort; the next Start creates a fresh socket.
            }
        }

        socket?.Dispose();
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var nextUrl = WebSocketUrl;
        var needsSubscription = true;
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                RaiseStatus(attempt == 0 ? "подключение к Twitch EventSub..." : "переподключение к Twitch EventSub...");
                var result = await ConnectAndListenAsync(nextUrl, needsSubscription, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.ReconnectUrl))
                {
                    nextUrl = result.ReconnectUrl;
                    needsSubscription = result.NeedsSubscription;
                    attempt = 0;
                    continue;
                }

                nextUrl = WebSocketUrl;
                needsSubscription = true;
                attempt++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("EventSub connection failed", ex);
                if (!_connectedReported)
                {
                    _initialConnection?.TrySetException(ex);
                    RaiseStatus("ошибка подключения чата: " + ex.Message);
                }
                else
                {
                    RaiseStatus("соединение потеряно, переподключение...");
                }

                nextUrl = WebSocketUrl;
                needsSubscription = true;
                attempt++;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, attempt * 3)));
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        RaiseStatus("чат отключён");
    }

    private async Task<ListenResult> ConnectAndListenAsync(string url, bool needsSubscription, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var socketUri) ||
            socketUri.Scheme != "wss" ||
            !socketUri.Host.Equals("eventsub.wss.twitch.tv", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(socketUri.UserInfo))
        {
            throw new InvalidOperationException("Twitch returned an invalid EventSub reconnect URL.");
        }

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        lock (_gate)
        {
            _socket = socket;
        }

        _logger.Info("WebSocket connecting to Twitch EventSub.");
        await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
        _logger.Info("WebSocket connected to Twitch EventSub.");
        var keepaliveSeconds = 30;
        var subscribed = !needsSubscription;

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string? text;
            using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                receiveCts.CancelAfter(TimeSpan.FromSeconds(keepaliveSeconds + 5));
                try
                {
                    text = await ReceiveTextAsync(socket, receiveCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Twitch EventSub keepalive timeout.");
                }
            }

            if (text is null)
            {
                return new ListenResult(null, true);
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var metadata = root.GetProperty("metadata");
            var messageType = GetString(metadata, "message_type");

            switch (messageType)
            {
                case "session_welcome":
                {
                    var session = root.GetProperty("payload").GetProperty("session");
                    var sessionId = GetString(session, "id");
                    keepaliveSeconds = GetInt(session, "keepalive_timeout_seconds") ?? keepaliveSeconds;
                    _logger.Info($"WebSocket session_welcome received: session_id={sessionId}");

                    if (needsSubscription && !subscribed)
                    {
                        _logger.Info($"Creating channel.chat.message subscription: broadcaster_user_id={_broadcasterId}, user_id={_chattingUserId}, session_id={sessionId}");
                        await _apiClient.CreateChatMessageSubscriptionAsync(sessionId, _broadcasterId, _chattingUserId, cancellationToken)
                            .ConfigureAwait(false);
                        subscribed = true;
                        _logger.Info("Subscription created successfully.");
                    }

                    MarkConnected();
                    break;
                }

                case "session_keepalive":
                    break;

                case "notification":
                    HandleNotification(root, metadata);
                    break;

                case "session_reconnect":
                {
                    var reconnectUrl = GetString(root.GetProperty("payload").GetProperty("session"), "reconnect_url");
                    if (!string.IsNullOrWhiteSpace(reconnectUrl))
                    {
                        RaiseStatus("Twitch запросил переподключение...");
                        return new ListenResult(reconnectUrl, false);
                    }

                    return new ListenResult(null, true);
                }

                case "revocation":
                    _initialConnection?.TrySetException(new InvalidOperationException("Twitch revoked EventSub subscription."));
                    RaiseStatus("Twitch отозвал подписку EventSub. Проверьте scopes и подключите Twitch заново.");
                    return new ListenResult(null, true);
            }
        }

        return new ListenResult(null, true);
    }

    private void HandleNotification(JsonElement root, JsonElement metadata)
    {
        var subscriptionType = GetString(metadata, "subscription_type");
        if (!string.Equals(subscriptionType, "channel.chat.message", StringComparison.Ordinal))
        {
            return;
        }

        var payload = root.GetProperty("payload");
        var evt = payload.GetProperty("event");
        var timestamp = ParseTimestamp(GetString(metadata, "message_timestamp"));
        var badges = new ObservableCollection<BadgeModel>();

        if (evt.TryGetProperty("badges", out var badgeArray) && badgeArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badgeArray.EnumerateArray())
            {
                badges.Add(new BadgeModel
                {
                    SetId = GetString(badge, "set_id"),
                    Id = GetString(badge, "id"),
                    Info = GetString(badge, "info")
                });
            }
        }

        var messageText = string.Empty;
        var parts = new ObservableCollection<ChatMessagePartModel>();
        if (evt.TryGetProperty("message", out var message) &&
            message.TryGetProperty("text", out var textElement))
        {
            messageText = textElement.GetString() ?? string.Empty;

            if (message.TryGetProperty("fragments", out var fragments) && fragments.ValueKind == JsonValueKind.Array)
            {
                foreach (var fragment in fragments.EnumerateArray())
                {
                    var fragmentText = GetString(fragment, "text");
                    var type = GetString(fragment, "type");
                    if (string.Equals(type, "emote", StringComparison.OrdinalIgnoreCase) &&
                        fragment.TryGetProperty("emote", out var emote) &&
                        emote.ValueKind == JsonValueKind.Object)
                    {
                        var isAnimated = emote.TryGetProperty("format", out var formats) &&
                                         formats.ValueKind == JsonValueKind.Array &&
                                         formats.EnumerateArray().Any(format =>
                                             string.Equals(format.GetString(), "animated", StringComparison.OrdinalIgnoreCase));
                        parts.Add(ChatMessagePartModel.TwitchEmote(fragmentText, GetString(emote, "id"), isAnimated));
                    }
                    else if (!string.IsNullOrEmpty(fragmentText))
                    {
                        parts.Add(ChatMessagePartModel.TextPart(fragmentText));
                    }
                }
            }
        }

        if (parts.Count == 0 && !string.IsNullOrEmpty(messageText))
        {
            parts.Add(ChatMessagePartModel.TextPart(messageText));
        }

        var chatMessage = new ChatMessageModel
        {
            Id = GetString(evt, "message_id"),
            Timestamp = timestamp,
            UserId = GetString(evt, "chatter_user_id"),
            Login = GetString(evt, "chatter_user_login"),
            DisplayName = GetString(evt, "chatter_user_name"),
            Text = messageText,
            Color = GetString(evt, "color"),
            Badges = badges,
            Parts = parts
        };

        MessageReceived?.Invoke(this, chatMessage);
    }
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    continue;
                }

                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaxEventPayloadBytes)
                {
                    throw new InvalidDataException("Twitch EventSub message exceeded the size limit.");
                }

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void RaiseStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void MarkConnected()
    {
        _connectedReported = true;
        _initialConnection?.TrySetResult(true);
        _logger.Info("Chat connected.");
        RaiseStatus("чат подключён");
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        var dot = value.IndexOf('.');
        var z = value.IndexOf('Z');
        if (dot >= 0 && z > dot + 8)
        {
            var trimmed = value[..(dot + 8)] + value[z..];
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private sealed record ListenResult(string? ReconnectUrl, bool NeedsSubscription);
}
