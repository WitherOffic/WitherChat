using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class OverlayServerService : IAsyncDisposable
{
    private const int MaxQueuedBroadcasts = 256;
    private const int MaxOverlayClients = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, OverlayClient> _clients = new();
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
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
                _ = BroadcastSettingsAsync();
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
        lock (_history)
        {
            _history.Add(dto);
            TrimHistory();
        }

        EnqueueBroadcast("message", dto);
    }

    public void PublishTestMessage()
    {
        var message = new ChatMessageModel
        {
            Id = "overlay-test-" + Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.Now,
            Login = "wither_101",
            DisplayName = "Wither_101",
            Text = "OBS overlay test message",
            Color = "#9F8CFF"
        };
        message.Parts.Add(ChatMessagePartModel.TextPart("OBS overlay test message"));
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
                $"Не удалось запустить OBS overlay на порту {port}. Порт занят или недоступен. Выберите другой порт в настройках.",
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

        foreach (var pair in _clients.ToArray())
        {
            await RemoveClientAsync(pair.Key).ConfigureAwait(false);
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch
            {
                // Listener shutdown throws when HttpListener is stopped.
            }

            _listenTask = null;
        }

        cts?.Dispose();
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true } listener)
        {
            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
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

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.RemoteEndPoint is null ||
                !IPAddress.IsLoopback(context.Request.RemoteEndPoint.Address))
            {
                context.Response.StatusCode = 403;
                await WriteTextAsync(context.Response, "Forbidden", "text/plain; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (!context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 405;
                context.Response.Headers["Allow"] = "GET";
                await WriteTextAsync(context.Response, "Method not allowed", "text/plain; charset=utf-8").ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? string.Empty;

            switch (path)
            {
                case "":
                case "/overlay/chat":
                    await WriteTextAsync(context.Response, BuildOverlayHtml(), "text/html; charset=utf-8").ConfigureAwait(false);
                    break;
                case "/overlay/events":
                    await HandleEventsAsync(context, cancellationToken).ConfigureAwait(false);
                    break;
                case "/overlay/history":
                    await WriteJsonAsync(context.Response, GetHistory()).ConfigureAwait(false);
                    break;
                case "/overlay/config":
                    await WriteJsonAsync(context.Response, CreateSettingsDto()).ConfigureAwait(false);
                    break;
                default:
                    context.Response.StatusCode = 404;
                    await WriteTextAsync(context.Response, "Not found", "text/plain; charset=utf-8").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"OBS overlay request failed: {ex.GetType().Name}");
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = 500;
                await WriteTextAsync(context.Response, "Overlay error", "text/plain; charset=utf-8").ConfigureAwait(false);
            }
        }
    }

    private async Task HandleEventsAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        if (!_clientSlots.Wait(0))
        {
            response.StatusCode = 503;
            await WriteTextAsync(response, "Too many overlay clients", "text/plain; charset=utf-8").ConfigureAwait(false);
            return;
        }

        var id = Guid.NewGuid();
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
            var client = new OverlayClient(writer);
            _clients[id] = client;

            await WriteEventAsync(client, "settings", CreateSettingsDto()).ConfigureAwait(false);
            await writer.WriteAsync(": connected\n\n").ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Browser Source disconnects are normal.
        }
        finally
        {
            await RemoveClientAsync(id).ConfigureAwait(false);
            response.Close();
            _clientSlots.Release();
        }
    }

    private async Task BroadcastSettingsAsync()
    {
        EnqueueBroadcast("settings", CreateSettingsDto());
        await Task.CompletedTask;
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

        foreach (var pair in _clients.ToArray())
        {
            try
            {
                await WriteEventJsonAsync(pair.Value, eventName, json).ConfigureAwait(false);
            }
            catch
            {
                await RemoveClientAsync(pair.Key).ConfigureAwait(false);
            }
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

    private async Task RemoveClientAsync(Guid id)
    {
        if (!_clients.TryRemove(id, out var client))
        {
            return;
        }

        try
        {
            await client.Writer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore disconnect cleanup failures.
        }

        client.Gate.Dispose();
    }

    private OverlayMessageDto CreateMessageDto(ChatMessageModel message)
    {
        return new OverlayMessageDto(
            message.Id,
            message.TimeText,
            message.Login,
            message.UserLabel,
            string.IsNullOrWhiteSpace(message.Color) ? "#B4B4FF" : message.Color,
            message.Text,
            message.Badges.Select(b => new OverlayBadgeDto(b.SetId, b.Id, b.ImageUrl, b.ToolTip)).ToArray(),
            message.Parts.Select(p => new OverlayPartDto(p.Kind.ToString(), p.Text, p.ImageUrl, p.ToolTip)).ToArray());
    }

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
            settings.OverlayBackgroundOpacity.ToString("0.###", CultureInfo.InvariantCulture),
            settings.OverlayAlign);
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

    private static async Task WriteJsonAsync<T>(HttpListenerResponse response, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await WriteTextAsync(response, json, "application/json; charset=utf-8").ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
                "img-src 'self' https://static-cdn.jtvnw.net https://cdn.betterttv.net https://cdn.7tv.app; " +
                "connect-src 'self'; object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";
        }
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static string BuildOverlayHtml() => """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>WitherChat Overlay</title>
  <style>
    :root {
      --font-size: 22px;
      --bg-opacity: 0;
      --align-items: flex-start;
      --text-align: left;
      --shadow: 0 2px 6px rgba(0, 0, 0, .85);
    }
    html, body {
      width: 100%;
      height: 100%;
      margin: 0;
      padding: 0;
      overflow: hidden;
      background: transparent;
      font-family: "Segoe UI Variable", "Segoe UI", sans-serif;
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
      padding: 8px 12px;
      border-radius: 16px;
      background: rgba(8, 10, 18, var(--bg-opacity));
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
      backgroundOpacity: '0',
      align: 'left'
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
      if (!safeSource) return;
      const image = document.createElement('img');
      image.className = className;
      image.src = safeSource;
      image.alt = String(alt ?? '');
      parent.appendChild(image);
    }

    function applySettings(next) {
      settings = { ...settings, ...(next || {}) };
      document.documentElement.style.setProperty('--font-size', `${settings.fontSize}px`);
      document.documentElement.style.setProperty('--bg-opacity', settings.backgroundOpacity);
      document.documentElement.style.setProperty('--shadow', settings.textShadow ? '0 2px 6px rgba(0, 0, 0, .85)' : 'none');
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

      message.parts.forEach(part => {
        const isEmote = part.kind !== 'Text';
        if (settings.showEmotes && isEmote && part.imageUrl) {
          appendImage(parent, 'emote', part.imageUrl, part.text);
        } else {
          parent.appendChild(document.createTextNode(String(part.text ?? '')));
        }
      });
    }

    function addMessage(message) {
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

    fetch('/overlay/config').then(r => r.json()).then(applySettings).catch(() => {});
    fetch('/overlay/history').then(r => r.json()).then(items => {
      if (Array.isArray(items)) items.forEach(addMessage);
    }).catch(() => {});

    const events = new EventSource('/overlay/events');
    events.addEventListener('settings', e => applySettings(JSON.parse(e.data)));
    events.addEventListener('message', e => addMessage(JSON.parse(e.data)));
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
    }

    private sealed record OverlayEvent(string EventName, string Json);

    private sealed record OverlayClient(StreamWriter Writer)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed record OverlaySettingsDto(
        int MaxMessages,
        string FontSize,
        bool ShowTimestamps,
        bool ShowBadges,
        bool ShowEmotes,
        int FadeOutSeconds,
        bool TextShadow,
        string BackgroundOpacity,
        string Align);

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
