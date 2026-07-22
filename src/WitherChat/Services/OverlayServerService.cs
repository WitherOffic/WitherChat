using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class OverlayServerService : IAsyncDisposable
{
    private const int MaxQueuedBroadcasts = 2048;
    private const int MaxConcurrentRequests = 32;
    private const int MaxOverlayClients = 8;
    private static readonly TimeSpan ClientCleanupTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<byte[]> TornBlackBackgroundAsset = new(LoadTornBlackBackgroundAsset);
    private static readonly Lazy<byte[]> InterFontAsset = new(() => LoadApplicationResource(
        "assets/fonts/inter-variable.ttf",
        "The embedded Inter font is missing."));
    private readonly ConcurrentDictionary<Guid, OverlayClient> _clients = new();
    private readonly ConcurrentDictionary<Task, byte> _requestTasks = new();
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly SemaphoreSlim _clientSlots = new(MaxOverlayClients, MaxOverlayClients);
    private readonly List<OverlayMessageDto> _history = new();
    private readonly Channel<OverlayEvent> _broadcastQueue;
    private readonly Task _broadcastTask;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private AppSettings _settings = new();
    private int _port;

    public OverlayServerService(FileLogger logger)
    {
        _logger = logger;
        _broadcastQueue = Channel.CreateBounded<OverlayEvent>(new BoundedChannelOptions(MaxQueuedBroadcasts)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _broadcastTask = Task.Run(ProcessBroadcastQueueAsync);
    }

    public bool IsRunning => _listener?.IsListening == true;
    public string Url => $"http://localhost:{_port}/overlay/chat";

    public async Task ConfigureAsync(AppSettings settings)
    {
        var snapshot = settings.Clone();
        snapshot.Normalize();

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _settings = snapshot;
            if (!snapshot.EnableObsOverlay)
            {
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }

            if (IsRunning && _port == snapshot.OverlayPort)
            {
                TrimHistory();
                BroadcastSettings();
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            StartCore(snapshot.OverlayPort);
            _logger.Info($"OBS overlay started: {Url}");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public void PublishMessage(ChatMessageModel message)
    {
        if (!IsRunning)
        {
            return;
        }

        var dto = CreateMessageDto(message);
        if (!HasRenderableContent(dto))
        {
            return;
        }

        lock (_history)
        {
            _history.Add(dto);
            TrimHistory();
        }

        EnqueueBroadcast("message", dto);
    }

    public void ClearMessages()
    {
        lock (_history)
        {
            _history.Clear();
        }

        EnqueueBroadcast("clear", new { });
    }

    public void PublishTestMessage()
    {
        var message = new ChatMessageModel
        {
            Id = "overlay-test-" + Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.Now,
            Login = "witherchat",
            DisplayName = "WitherChat",
            Text = LocalizationService.Get(_settings.Language, "TestOverlayMessage"),
            Color = "#9F8CFF"
        };
        message.Parts.Add(ChatMessagePartModel.TextPart(message.Text));
        PublishMessage(message);
    }

    private void StartCore(int port)
    {
        _port = port;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _listener.Close();
            _listener = null;
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationService.Get(_settings.Language, "OverlayStartFailedFormat"),
                    port),
                ex);
        }

        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            cts.Cancel();
        }

        if (_listener is not null)
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // Stopping the local overlay server must be best effort.
            }

            _listener = null;
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask
                    .WaitAsync(RequestShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warn("OBS overlay listener did not stop before the shutdown deadline.");
            }
            catch
            {
                // Listener shutdown throws when HttpListener is stopped.
            }

            _listenTask = null;
        }

        var clientRemovals = _clients.Keys
            .Select(RemoveClientAsync)
            .ToArray();
        if (clientRemovals.Length > 0)
        {
            await Task.WhenAll(clientRemovals).ConfigureAwait(false);
        }

        var requestsDrained = await WaitForRequestTasksAsync().ConfigureAwait(false);

        if (requestsDrained)
        {
            cts?.Dispose();
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                if (!_requestSlots.Wait(0, CancellationToken.None))
                {
                    RejectOverloadedRequest(context);
                    continue;
                }

                TrackRequest(HandleRequestInSlotAsync(context, cancellationToken));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"OBS overlay listener failed: {ex.GetType().Name}");
            }
        }
    }

    private async Task HandleRequestInSlotAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestSafelyAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestSlots.Release();
        }
    }

    private static void RejectOverloadedRequest(HttpListenerContext context)
    {
        try
        {
            var response = context.Response;
            var body = Encoding.UTF8.GetBytes("Overlay server is busy");
            response.StatusCode = 503;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Retry-After"] = "1";
            response.ContentLength64 = body.Length;
            response.Close(body, willBlock: false);
        }
        catch
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // The peer may already have disconnected.
            }
        }
    }

    private void TrackRequest(Task task)
    {
        _requestTasks.TryAdd(task, 0);
        _ = task.ContinueWith(
            completed =>
            {
                _requestTasks.TryRemove(completed, out _);
                if (completed.Exception is { } exception)
                {
                    _logger.Warn($"OBS overlay request task failed: {exception.GetBaseException().GetType().Name}");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<bool> WaitForRequestTasksAsync()
    {
        var tasks = _requestTasks.Keys.ToArray();
        if (tasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(tasks)
                .WaitAsync(RequestShutdownTimeout)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.Warn($"OBS overlay shutdown timed out with {_requestTasks.Count} active request(s).");
            return false;
        }
        catch
        {
            // Individual failures are observed and logged by TrackRequest.
            return true;
        }
    }

    private async Task HandleRequestSafelyAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // The response may already be closed by the request handler.
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.RemoteEndPoint is null ||
                !IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
            {
                context.Response.StatusCode = 403;
                await WriteTextAsync(
                        context.Response,
                        "Forbidden",
                        "text/plain; charset=utf-8",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 405;
                context.Response.Headers["Allow"] = "GET";
                await WriteTextAsync(
                        context.Response,
                        "Method not allowed",
                        "text/plain; charset=utf-8",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;

            switch (path)
            {
                case "":
                case "/overlay/chat":
                    await WriteTextAsync(
                            context.Response,
                            BuildOverlayHtml(),
                            "text/html; charset=utf-8",
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "/overlay/events":
                    await HandleEventsAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case "/overlay/history":
                    await WriteJsonAsync(context.Response, GetHistory(), cancellationToken).ConfigureAwait(false);
                    break;
                case "/overlay/config":
                    await WriteJsonAsync(context.Response, CreateSettingsDto(), cancellationToken).ConfigureAwait(false);
                    break;
                case "/overlay/assets/message-torn-black.png":
                    await WriteBytesAsync(
                            context.Response,
                            TornBlackBackgroundAsset.Value,
                            "image/png",
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "/overlay/assets/inter-variable.ttf":
                    await WriteBytesAsync(
                            context.Response,
                            InterFontAsset.Value,
                            "font/ttf",
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    await WriteTextAsync(
                            context.Response,
                            "Not found",
                            "text/plain; charset=utf-8",
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // The peer or listener may already be closed.
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"OBS overlay request failed: {ex.GetType().Name}");
            try
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    context.Response.StatusCode = 500;
                    await WriteTextAsync(
                            context.Response,
                            "Overlay error",
                            "text/plain; charset=utf-8",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                    // The response is already unavailable.
                }
            }
        }
    }

    private async Task HandleEventsAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        if (!_clientSlots.Wait(0, cancellationToken))
        {
            var oldestClientId = _clients.Keys.FirstOrDefault();
            if (oldestClientId != Guid.Empty)
            {
                await RemoveClientAsync(oldestClientId).ConfigureAwait(false);
            }

            if (!await _clientSlots.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
            {
                response.StatusCode = 503;
                await WriteTextAsync(
                        response,
                        "Too many overlay clients",
                        "text/plain; charset=utf-8",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        var id = Guid.NewGuid();
        OverlayClient? client = null;
        try
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            response.SendChunked = true;

            var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = false
            };
            client = new OverlayClient(writer, response);
            OverlayMessageDto[] initialHistory;
            lock (_history)
            {
                // Registering the client and taking the snapshot under the same lock as
                // PublishMessage prevents a gap between history and live SSE events.
                _clients[id] = client;
                initialHistory = _history.ToArray();
            }
            using var clientCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                client.Disconnect.Token);
            var clientToken = clientCancellation.Token;

            await WriteEventAsync(client, "settings", CreateSettingsDto()).ConfigureAwait(false);
            await WriteEventAsync(client, "history", initialHistory).ConfigureAwait(false);
            while (!clientToken.IsCancellationRequested)
            {
                await WriteHeartbeatAsync(client, clientToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), clientToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Browser Source disconnects are normal.
        }
        finally
        {
            await RemoveClientAsync(id).ConfigureAwait(false);
            client?.Gate.Dispose();
            client?.Disconnect.Dispose();
            try
            {
                response.Close();
            }
            finally
            {
                _clientSlots.Release();
            }
        }
    }

    private void BroadcastSettings()
    {
        EnqueueBroadcast("settings", CreateSettingsDto());
    }

    private void EnqueueBroadcast<T>(string eventName, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions).Replace("\r", string.Empty).Replace("\n", string.Empty);
        _broadcastQueue.Writer.TryWrite(new OverlayEvent(eventName, json));
    }

    private async Task ProcessBroadcastQueueAsync()
    {
        await foreach (var item in _broadcastQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await BroadcastJsonAsync(item.EventName, item.Json).ConfigureAwait(false);
        }
    }

    private async Task BroadcastJsonAsync(string eventName, string json)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var writes = _clients.ToArray()
            .Select(pair => WriteToClientSafelyAsync(pair.Key, pair.Value, eventName, json));
        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    private async Task WriteToClientSafelyAsync(
        Guid clientId,
        OverlayClient client,
        string eventName,
        string json)
    {
        try
        {
            await WriteEventJsonAsync(client, eventName, json).ConfigureAwait(false);
        }
        catch
        {
            await RemoveClientAsync(clientId).ConfigureAwait(false);
        }
    }

    private static async Task WriteEventAsync<T>(OverlayClient client, string eventName, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions).Replace("\r", string.Empty).Replace("\n", string.Empty);
        await WriteEventJsonAsync(client, eventName, json).ConfigureAwait(false);
    }

    private static async Task WriteEventJsonAsync(OverlayClient client, string eventName, string json)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            await client.Gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            entered = true;
            await client.Writer.WriteAsync("event: ".AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.WriteAsync(eventName.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.WriteAsync("\n".AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.WriteAsync("data: ".AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.WriteAsync(json.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.WriteAsync("\n\n".AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                client.Gate.Release();
            }
        }
    }

    private static async Task WriteHeartbeatAsync(OverlayClient client, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            await client.Gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            entered = true;
            await client.Writer.WriteAsync(": heartbeat\n\n".AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await client.Writer.FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                client.Gate.Release();
            }
        }
    }

    private async Task RemoveClientAsync(Guid id)
    {
        if (!_clients.TryRemove(id, out var client))
        {
            return;
        }

        try
        {
            client.Disconnect.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            // Abort the HTTP response first so a stalled browser cannot keep a
            // StreamWriter flush blocked during client removal or app shutdown.
            client.Response.Abort();
        }
        catch
        {
            // Ignore disconnect cleanup failures.
        }

        try
        {
            var disposeTask = client.Writer.DisposeAsync().AsTask();
            try
            {
                await disposeTask.WaitAsync(ClientCleanupTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                ObserveFault(disposeTask);
            }
        }
        catch
        {
            // Ignore disconnect cleanup failures.
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private OverlayMessageDto CreateMessageDto(ChatMessageModel message)
    {
        var text = message.IsChannelPointsMessage && !string.IsNullOrWhiteSpace(message.RewardUserInput)
            ? message.RewardUserInput
            : message.Text;
        return new OverlayMessageDto(
            message.Id,
            message.TimeText,
            message.Login,
            message.UserLabel,
            string.IsNullOrWhiteSpace(message.Color) ? "#B4B4FF" : message.Color,
            text,
            message.Badges.Select(b => new OverlayBadgeDto(b.SetId, b.Id, b.ImageUrl, b.ToolTip)).ToArray(),
            message.Parts.Select(p => new OverlayPartDto(p.Kind.ToString(), p.Text, p.ImageUrl, p.ToolTip)).ToArray());
    }

    private static bool HasRenderableContent(OverlayMessageDto message) =>
        !string.IsNullOrWhiteSpace(message.Text) ||
        message.Parts.Any(part =>
            !string.IsNullOrWhiteSpace(part.Text) ||
            !string.IsNullOrWhiteSpace(part.ImageUrl));

    private OverlaySettingsDto CreateSettingsDto()
    {
        var settings = GetSettingsSnapshot();
        return new OverlaySettingsDto(
            settings.OverlayMaxMessages,
            settings.OverlayFontSize.ToString("0.##", CultureInfo.InvariantCulture),
            settings.OverlayShowTimestamps,
            settings.OverlayShowBadges,
            settings.OverlayShowEmotes,
            settings.OverlayFadeOutSeconds,
            settings.OverlayTextShadow,
            settings.OverlayTextOutline,
            settings.OverlayDarkBackground,
            settings.OverlayBackgroundOpacity.ToString("0.###", CultureInfo.InvariantCulture),
            settings.OverlayAlign,
            string.Equals(settings.MessageVisualTheme, "TornBlack", StringComparison.OrdinalIgnoreCase)
                ? "torn-black"
                : "default",
            GetOverlayFontFamily(settings.UiFontFamily));
    }

    private OverlayMessageDto[] GetHistory()
    {
        lock (_history)
        {
            return _history.ToArray();
        }
    }

    private AppSettings GetSettingsSnapshot()
    {
        return _settings.Clone();
    }

    private void TrimHistory()
    {
        var max = Math.Clamp(_settings.OverlayMaxMessages, 1, 100);
        lock (_history)
        {
            while (_history.Count > max)
            {
                _history.RemoveAt(0);
            }
        }
    }

    private static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        T value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteTextAsync(
                response,
                json,
                "application/json; charset=utf-8",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        string text,
        string contentType,
        CancellationToken cancellationToken) =>
        await WriteBytesAsync(response, Encoding.UTF8.GetBytes(text), contentType, cancellationToken).ConfigureAwait(false);

    private static async Task WriteBytesAsync(
        HttpListenerResponse response,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        response.ContentType = contentType;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cache-Control"] = "no-store";
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                "img-src 'self' https://static-cdn.jtvnw.net https://cdn.betterttv.net https://cdn.7tv.app; " +
                "font-src 'self'; " +
                "connect-src 'self'; object-src 'none'; frame-src 'none'; frame-ancestors 'none'; " +
                "base-uri 'none'; form-action 'none'";
        }
        response.ContentLength64 = bytes.Length;

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeCts.CancelAfter(ResponseWriteTimeout);
        Task? writeTask = null;
        try
        {
            writeTask = response.OutputStream
                .WriteAsync(bytes.AsMemory(), writeCts.Token)
                .AsTask();
            await writeTask.WaitAsync(writeCts.Token).ConfigureAwait(false);
            response.Close();
        }
        catch (OperationCanceledException)
        {
            try
            {
                response.Abort();
            }
            catch
            {
                // The response may already be closed by the listener.
            }

            if (writeTask is { IsCompleted: false })
            {
                ObserveFault(writeTask);
            }

            throw;
        }
    }

    private static byte[] LoadTornBlackBackgroundAsset() =>
        LoadApplicationResource(
            "assets/themes/chat_message_torn_black_overlay.png",
            "The torn black message theme asset is missing.");

    private static byte[] LoadApplicationResource(string key, string missingMessage)
    {
        var assembly = typeof(OverlayServerService).Assembly;
        var resourceName = $"{assembly.GetName().Name}.g.resources";
        using var manifestStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The application resource bundle is missing.");
        using var resourceSet = new System.Resources.ResourceSet(manifestStream);
        var stream = resourceSet.GetObject(key, ignoreCase: false) as Stream
            ?? throw new InvalidOperationException(missingMessage);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string GetOverlayFontFamily(string fontId) => fontId switch
    {
        "Inter" => "WitherChat Inter, Inter, Segoe UI, sans-serif",
        "SegoeUI" => "Segoe UI, sans-serif",
        "Aptos" => "Aptos, Segoe UI, sans-serif",
        "Bahnschrift" => "Bahnschrift, Segoe UI, sans-serif",
        "Calibri" => "Calibri, Segoe UI, sans-serif",
        "Candara" => "Candara, Segoe UI, sans-serif",
        "Trebuchet" => "Trebuchet MS, Segoe UI, sans-serif",
        _ => "Segoe UI Variable, Segoe UI, sans-serif"
    };

    private string BuildOverlayHtml() => OverlayHtml.Replace(
        "__LANG__",
        string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru",
        StringComparison.Ordinal);

    private const string OverlayHtml = """
<!doctype html>
<html lang="__LANG__">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>WitherChat Overlay</title>
  <style>
    @font-face {
      font-family: "WitherChat Inter";
      src: url('/overlay/assets/inter-variable.ttf') format('truetype');
      font-style: normal;
      font-weight: 100 900;
      font-display: swap;
    }
    :root {
      --font-size: 22px;
      --font-family: "Segoe UI Variable", "Segoe UI", sans-serif;
      --align-items: flex-start;
      --text-align: left;
      --shadow: 0 2px 6px rgba(0, 0, 0, .85);
      --message-background: linear-gradient(transparent, transparent);
      --message-theme-image: none;
      --message-border: 1px solid transparent;
      --message-box-shadow: none;
      --message-padding: 8px 12px;
      --message-width: auto;
      --message-radius: 16px;
    }
    html, body {
      width: 100%;
      height: 100%;
      margin: 0;
      padding: 0;
      overflow: hidden;
      background: transparent;
      font-family: var(--font-family);
    }
    #chat {
      box-sizing: border-box;
      width: 100%;
      height: 100%;
      padding: 14px;
      display: flex;
      flex-direction: column;
      justify-content: flex-end;
      align-items: var(--align-items);
      gap: 8px;
    }
    .message {
      max-width: 100%;
      box-sizing: border-box;
      width: var(--message-width);
      padding: var(--message-padding);
      border-radius: var(--message-radius);
      border: var(--message-border);
      background-image: var(--message-theme-image), var(--message-background);
      background-position: center, center;
      background-repeat: no-repeat, no-repeat;
      background-size: 100% 100%, 100% 100%;
      box-shadow: var(--message-box-shadow);
      color: #fff;
      font-size: var(--font-size);
      line-height: 1.28;
      text-align: var(--text-align);
      text-shadow: var(--shadow);
      word-break: break-word;
      animation: enter .18s ease-out;
    }
    .time {
      margin-right: 8px;
      color: rgba(230, 235, 255, .62);
      font-size: .72em;
      font-weight: 500;
    }
    .badge {
      width: 1em;
      height: 1em;
      margin-right: 4px;
      vertical-align: -0.12em;
    }
    .name {
      margin-right: 8px;
      font-weight: 800;
    }
    .emote {
      height: 1.35em;
      width: auto;
      vertical-align: -0.32em;
      margin: 0 2px;
    }
    .fade {
      opacity: 0;
      transform: translateY(-6px);
      transition: opacity .35s ease, transform .35s ease;
    }
    @keyframes enter {
      from { opacity: 0; transform: translateY(8px) scale(.99); }
      to { opacity: 1; transform: translateY(0) scale(1); }
    }
  </style>
</head>
<body>
  <div id="chat"></div>
  <script>
    const chat = document.getElementById('chat');
    let settings = {
      maxMessages: 12,
      fontSize: '22',
      showTimestamps: true,
      showBadges: true,
      showEmotes: true,
      fadeOutSeconds: 0,
      textShadow: true,
      textOutline: true,
      darkBackground: true,
      backgroundOpacity: '0',
      align: 'left',
      messageTheme: 'torn-black',
      fontFamily: 'Segoe UI Variable, Segoe UI, sans-serif'
    };

    const allowedImageHosts = new Set(['static-cdn.jtvnw.net', 'cdn.betterttv.net', 'cdn.7tv.app']);

    function safeImageUrl(value) {
      try {
        const url = new URL(String(value ?? ''));
        return url.protocol === 'https:' && allowedImageHosts.has(url.hostname) ? url.href : null;
      } catch {
        return null;
      }
    }

    function appendImage(parent, className, source, alt) {
      const safeSource = safeImageUrl(source);
      if (!safeSource) return false;
      const image = document.createElement('img');
      image.className = className;
      image.src = safeSource;
      image.alt = String(alt ?? '');
      parent.appendChild(image);
      return true;
    }

    function applySettings(next) {
      settings = { ...settings, ...(next || {}) };
      document.documentElement.style.setProperty('--font-size', `${settings.fontSize}px`);
      document.documentElement.style.setProperty('--font-family', settings.fontFamily);
      const requestedOpacity = Number.parseFloat(settings.backgroundOpacity);
      const backgroundOpacity = Number.isFinite(requestedOpacity) ? Math.min(1, Math.max(0, requestedOpacity)) : 0;
      const useTornBlackTheme = settings.messageTheme === 'torn-black';
      const styledOpacity = settings.darkBackground && !useTornBlackTheme ? Math.max(.48, backgroundOpacity) : 0;
      document.documentElement.style.setProperty(
        '--message-theme-image',
        useTornBlackTheme ? "url('/overlay/assets/message-torn-black.png')" : 'none');
      document.documentElement.style.setProperty('--message-padding', useTornBlackTheme ? '16px 24px 18px' : '8px 12px');
      document.documentElement.style.setProperty('--message-width', useTornBlackTheme ? '100%' : 'auto');
      document.documentElement.style.setProperty('--message-radius', useTornBlackTheme ? '0' : '16px');
      document.documentElement.style.setProperty(
        '--message-background',
        settings.darkBackground && !useTornBlackTheme
          ? `linear-gradient(135deg, rgba(5, 7, 13, ${styledOpacity}), rgba(12, 16, 27, ${Math.min(1, styledOpacity + .1)}))`
          : 'linear-gradient(transparent, transparent)');
      document.documentElement.style.setProperty(
        '--message-border',
        settings.darkBackground && !useTornBlackTheme ? '1px solid rgba(255, 255, 255, .1)' : '1px solid transparent');
      document.documentElement.style.setProperty(
        '--message-box-shadow',
        settings.darkBackground && !useTornBlackTheme ? '0 8px 22px rgba(0, 0, 0, .28)' : 'none');
      const dropShadow = settings.textShadow ? '0 2px 6px rgba(0, 0, 0, .9)' : '';
      const outline = settings.textOutline
        ? '-1px -1px 0 rgba(0, 0, 0, .95), 1px -1px 0 rgba(0, 0, 0, .95), -1px 1px 0 rgba(0, 0, 0, .95), 1px 1px 0 rgba(0, 0, 0, .95)'
        : '';
      document.documentElement.style.setProperty('--shadow', [outline, dropShadow].filter(Boolean).join(', ') || 'none');
      const align = settings.align === 'center' ? 'center' : settings.align === 'right' ? 'flex-end' : 'flex-start';
      document.documentElement.style.setProperty('--align-items', align);
      document.documentElement.style.setProperty('--text-align', settings.align === 'center' ? 'center' : settings.align === 'right' ? 'right' : 'left');
      trim();
    }

    function renderParts(message, parent) {
      if (!Array.isArray(message.parts) || message.parts.length === 0) {
        parent.appendChild(document.createTextNode(String(message.text ?? '')));
        return;
      }

      let rendered = false;
      message.parts.forEach(part => {
        const isEmote = part.kind !== 'Text';
        if (settings.showEmotes && isEmote && part.imageUrl) {
          if (appendImage(parent, 'emote', part.imageUrl, part.text)) {
            rendered = true;
          } else {
            const fallback = String(part.text ?? '');
            if (fallback) {
              parent.appendChild(document.createTextNode(fallback));
              rendered = true;
            }
          }
        } else {
          const text = String(part.text ?? '');
          if (text) {
            parent.appendChild(document.createTextNode(text));
            rendered = true;
          }
        }
      });
      if (!rendered && message.text) {
        parent.appendChild(document.createTextNode(String(message.text)));
      }
    }

    const seenMessageIds = new Set();
    const seenMessageOrder = [];

    function rememberMessage(message) {
      const id = String(message?.id ?? '');
      if (!id) return true;
      if (seenMessageIds.has(id)) return false;
      seenMessageIds.add(id);
      seenMessageOrder.push(id);
      const keep = Math.max(100, (Number(settings.maxMessages) || 12) * 4);
      while (seenMessageOrder.length > keep) {
        seenMessageIds.delete(seenMessageOrder.shift());
      }
      return true;
    }

    function addMessage(message) {
      if (!message || !rememberMessage(message)) return;
      const row = document.createElement('div');
      row.className = 'message';
      row.dataset.id = message.id || `${Date.now()}-${Math.random()}`;
      if (settings.showTimestamps) {
        const time = document.createElement('span');
        time.className = 'time';
        time.textContent = String(message.time ?? '');
        row.appendChild(time);
      }
      if (settings.showBadges && Array.isArray(message.badges)) {
        message.badges.forEach(b => appendImage(row, 'badge', b.imageUrl, b.title || b.setId));
      }
      const name = document.createElement('span');
      name.className = 'name';
      const color = String(message.color ?? '');
      name.style.color = /^#[0-9a-f]{6}$/i.test(color) ? color : '#B4B4FF';
      name.textContent = String(message.displayName || message.login || '');
      row.appendChild(name);
      const text = document.createElement('span');
      text.className = 'text';
      renderParts(message, text);
      row.appendChild(text);
      chat.appendChild(row);
      trim();
      if (settings.fadeOutSeconds > 0) {
        window.setTimeout(() => {
          row.classList.add('fade');
          window.setTimeout(() => row.remove(), 420);
        }, settings.fadeOutSeconds * 1000);
      }
    }

    function trim() {
      const max = Math.max(1, Number(settings.maxMessages) || 12);
      while (chat.children.length > max) {
        chat.removeChild(chat.firstElementChild);
      }
    }

    const events = new EventSource('/overlay/events');
    events.addEventListener('settings', e => applySettings(JSON.parse(e.data)));
    events.addEventListener('history', e => {
      const items = JSON.parse(e.data);
      if (Array.isArray(items)) items.forEach(addMessage);
    });
    events.addEventListener('message', e => addMessage(JSON.parse(e.data)));
    events.addEventListener('clear', () => {
      chat.replaceChildren();
      seenMessageIds.clear();
      seenMessageOrder.length = 0;
    });
  </script>
</body>
</html>
""";

    public async ValueTask DisposeAsync()
    {
        _broadcastQueue.Writer.TryComplete();
        await StopAsync().ConfigureAwait(false);
        try
        {
            await _broadcastTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"OBS overlay broadcast shutdown failed: {ex.GetType().Name}");
        }
        _stateLock.Dispose();
        if (_requestTasks.IsEmpty)
        {
            _requestSlots.Dispose();
            _clientSlots.Dispose();
        }
        else
        {
            _logger.Warn("OBS overlay retained request synchronization resources for unfinished requests.");
        }
    }

    private sealed record OverlayEvent(string EventName, string Json);

    private sealed record OverlayClient(StreamWriter Writer, HttpListenerResponse Response)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Disconnect { get; } = new();
    }

    private sealed record OverlaySettingsDto(
        int MaxMessages,
        string FontSize,
        bool ShowTimestamps,
        bool ShowBadges,
        bool ShowEmotes,
        int FadeOutSeconds,
        bool TextShadow,
        bool TextOutline,
        bool DarkBackground,
        string BackgroundOpacity,
        string Align,
        string MessageTheme,
        string FontFamily);

    private sealed record OverlayMessageDto(
        string Id,
        string Time,
        string Login,
        string DisplayName,
        string Color,
        string Text,
        OverlayBadgeDto[] Badges,
        OverlayPartDto[] Parts);

    private sealed record OverlayBadgeDto(string SetId, string Id, string ImageUrl, string Title);

    private sealed record OverlayPartDto(string Kind, string Text, string ImageUrl, string ToolTip);
}
