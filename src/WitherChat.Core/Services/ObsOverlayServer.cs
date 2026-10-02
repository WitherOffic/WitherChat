using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class ObsOverlayServer : IAsyncDisposable
{
    private const int MaximumSubscribers = 32;
    private static readonly Lazy<byte[]> TornBlackBackgroundAsset = new(LoadTornBlackBackgroundAsset);
    private static readonly Lazy<byte[]> InterFontAsset = new(() => LoadEmbeddedAsset("Inter-Variable.ttf"));
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private readonly Queue<OverlayHistoryEntry> _history = new();
    private readonly object _historyGate = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _serverTask;
    private ObsOverlayOptions _options = new(
        17655, 12, 22, true, true, true, 0, true, true, true, 0, "left", "TornBlack");

    public bool IsRunning => _listener?.IsListening == true;
    public string Url => $"http://localhost:{_options.Port}/overlay/chat";

    public async Task ConfigureAsync(bool enabled, ObsOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var restart = IsRunning && options.Port != _options.Port;
            _options = options;
            lock (_historyGate)
            {
                while (_history.Count > Math.Max(1, options.MaximumMessages))
                {
                    _ = _history.Dequeue();
                }
            }
            if (!enabled)
            {
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }

            if (restart)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
            if (!IsRunning)
            {
                StartCore();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Publish(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsRunning)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            id = message.Id,
            channel = message.Channel,
            time = message.Timestamp.LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            user = message.UserLabel,
            color = message.UserColor,
            text = message.Text,
            rewardTitle = message.RewardTitle,
            rewardCost = message.RewardCost,
            eventKind = message.StreamEvent?.Kind,
            eventPlatform = message.StreamEvent?.Platform,
            eventAmount = message.StreamEvent?.AmountDisplay,
            badges = message.Badges.Select(badge => new
            {
                label = GetOverlayBadgeLabel(badge),
                image = badge.ImageUri?.AbsoluteUri
            }).ToArray(),
            parts = message.Parts.Select(part => new
            {
                kind = part.Kind == ChatMessagePartKind.Emote ? "emote" : "text",
                text = part.Text,
                image = part.ImageUri?.AbsoluteUri,
                provider = part.Provider,
                zeroWidth = part.IsZeroWidth
            }).ToArray()
        });
        lock (_historyGate)
        {
            _history.Enqueue(new OverlayHistoryEntry(NormalizeChannel(message.Channel), payload));
            while (_history.Count > Math.Max(1, _options.MaximumMessages))
            {
                _ = _history.Dequeue();
            }
        }
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(payload);
        }
    }

    public void ClearChannel(string channel)
    {
        channel = NormalizeChannel(channel);
        if (channel.Length == 0)
        {
            return;
        }

        lock (_historyGate)
        {
            if (_history.Count > 0)
            {
                var retained = _history
                    .Where(entry => !string.Equals(entry.Channel, channel, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                _history.Clear();
                foreach (var entry in retained)
                {
                    _history.Enqueue(entry);
                }
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            type = "clear",
            channel
        });
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(payload);
        }
    }

    private static string GetOverlayBadgeLabel(ChatBadge badge) => badge.Title switch
    {
        "witherchat.youtube.owner" => "YouTube channel owner",
        "witherchat.youtube.moderator" => "YouTube moderator",
        "witherchat.youtube.sponsor" => "YouTube channel member",
        { Length: > 0 } title => title,
        _ => badge.SetId
    };

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    private void StartCore()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_options.Port}/");
        listener.Start();
        _listener = listener;
        _cancellation = new CancellationTokenSource();
        _serverTask = RunAsync(listener, _cancellation.Token);
    }

    private async Task StopCoreAsync()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        var listener = Interlocked.Exchange(ref _listener, null);
        var task = Interlocked.Exchange(ref _serverTask, null);
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        listener?.Close();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        cancellation.Dispose();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryComplete();
        }
        _subscribers.Clear();
    }

    private async Task RunAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _ = HandleRequestAsync(context, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.RemoteEndPoint is not { Address: { } address } || !IPAddress.IsLoopback(address))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            if (context.Request.Url?.AbsolutePath == "/overlay/chat")
            {
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; " +
                    "script-src 'unsafe-inline'; " +
                    "style-src 'unsafe-inline'; " +
                    "font-src 'self'; " +
                    "img-src 'self' https://static-cdn.jtvnw.net https://cdn.betterttv.net " +
                    "https://cdn.7tv.app https://*.ggpht.com; " +
                    "connect-src 'self'; base-uri 'none'; form-action 'none'; object-src 'none'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
                await WriteAsync(context.Response, BuildHtml(_options), "text/html; charset=utf-8", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (context.Request.Url?.AbsolutePath == "/overlay/events")
            {
                if (_subscribers.Count >= MaximumSubscribers)
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    return;
                }
                await StreamEventsAsync(context.Response, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (context.Request.Url?.AbsolutePath == "/overlay/settings")
            {
                await WriteAsync(
                        context.Response,
                        BuildSettingsJson(_options),
                        "application/json; charset=utf-8",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (context.Request.Url?.AbsolutePath == "/overlay/assets/message-torn-black.png")
            {
                await WriteAsync(
                        context.Response,
                        TornBlackBackgroundAsset.Value,
                        "image/png",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (context.Request.Url?.AbsolutePath == "/overlay/assets/inter-variable.ttf")
            {
                await WriteAsync(
                        context.Response,
                        InterFontAsset.Value,
                        "font/ttf",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            try { context.Response.Close(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task StreamEventsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        response.ContentEncoding = Encoding.UTF8;
        response.SendChunked = true;
        response.KeepAlive = true;
        response.Headers["Cache-Control"] = "no-cache";
        var id = Guid.NewGuid();
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(250)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        string[] history;
        lock (_historyGate)
        {
            history = _history.Select(entry => entry.Payload).ToArray();
            _subscribers[id] = queue;
        }
        try
        {
            var ready = Encoding.UTF8.GetBytes(": connected\n\n");
            await response.OutputStream.WriteAsync(ready, cancellationToken).ConfigureAwait(false);
            foreach (var message in history)
            {
                var bytes = Encoding.UTF8.GetBytes("data: " + message + "\n\n");
                await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                string? message = null;
                using (var heartbeatCancellation =
                       CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    heartbeatCancellation.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        message = await queue.Reader.ReadAsync(heartbeatCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(
                    message is null ? ": keepalive\n\n" : "data: " + message + "\n\n");
                await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            response.Close();
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string value, string contentType, CancellationToken token)
    {
        await WriteAsync(response, Encoding.UTF8.GetBytes(value), contentType, token).ConfigureAwait(false);
    }

    private static async Task WriteAsync(
        HttpListenerResponse response,
        byte[] bytes,
        string contentType,
        CancellationToken token)
    {
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
        response.Close();
    }

    private static byte[] LoadTornBlackBackgroundAsset() =>
        LoadEmbeddedAsset("chat_message_torn_black_overlay.png");

    private static byte[] LoadEmbeddedAsset(string fileName)
    {
        var assembly = typeof(ObsOverlayServer).Assembly;
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name =>
            name.EndsWith(fileName, StringComparison.Ordinal));
        if (resourceName is null)
        {
            return [];
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return [];
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string BuildSettingsJson(ObsOverlayOptions options) =>
        JsonSerializer.Serialize(new
        {
            maximumMessages = options.MaximumMessages,
            fontSize = options.FontSize,
            showTimestamps = options.ShowTimestamps,
            showBadges = options.ShowBadges,
            showEmotes = options.ShowEmotes,
            fadeOutMilliseconds = options.FadeOutSeconds * 1000,
            textShadow = options.TextShadow,
            textOutline = options.TextOutline,
            darkBackground = options.DarkBackground,
            backgroundOpacity = options.BackgroundOpacity,
            alignment = options.Alignment,
            messageTheme = options.MessageTheme
        });

    private static string NormalizeChannel(string? channel) =>
        (channel ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();

    private static string BuildHtml(ObsOverlayOptions options) => $$$"""
<!doctype html><html><head><meta charset="utf-8"><style>
@font-face{font-family:Inter;src:url('/overlay/assets/inter-variable.ttf') format('truetype');font-style:normal;font-weight:100 900;font-display:swap}
html,body{width:100%;height:100%;margin:0;background:transparent;overflow:hidden;font-family:Inter,"Segoe UI",sans-serif;color:white}
#chat{width:100vw;max-width:100vw;height:100vh;overflow:hidden;display:flex;flex-direction:column;justify-content:flex-end;align-items:var(--align,{{{options.Alignment}}});gap:6px;padding:12px;box-sizing:border-box}
.m{box-sizing:border-box;min-width:0;max-width:100%;overflow-wrap:anywhere;word-break:break-word;white-space:normal;font-size:var(--font-size,{{{options.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}px);line-height:1.35;padding:var(--message-padding,4px 8px);border-style:solid;border-width:var(--message-border-width,0);border-color:transparent;border-image-source:var(--message-border-image,none);border-image-slice:55 100 fill;border-image-repeat:stretch;border-radius:var(--message-radius,8px);background-color:rgba(0,0,0,var(--background-opacity,0));background-image:var(--message-left-fill,none);background-position:left center;background-size:var(--message-left-fill-size,0 0);background-repeat:no-repeat;text-shadow:var(--text-shadow,none);-webkit-text-stroke:var(--text-outline,0 transparent);paint-order:stroke fill}
.time{opacity:.68;font-size:.72em;margin-right:7px}.badge{font-size:.62em;background:#4a55a8;border-radius:4px;padding:2px 4px;margin-right:4px}.user{font-weight:700;margin-right:6px}.emote{height:1.45em;vertical-align:middle}.emote-stack{display:inline-grid;place-items:center;vertical-align:middle;margin:0 .06em}.emote-stack>.emote{grid-area:1/1;max-width:7em;object-fit:contain}
.m.event{border-left:3px solid #a970ff;background-color:rgba(17,20,31,.82)}.m.event.youtube{border-left-color:#ff4e45}.m.event.donationalerts{border-left-color:#ff8a1f}.event-title{font-size:.72em;font-weight:800;text-transform:uppercase;letter-spacing:.04em;color:#bda7ff;margin-right:7px}.event-amount{font-weight:800;color:#ffad66;margin-left:7px}
</style></head><body><div id="chat"></div><script>
const root=document.getElementById('chat'),seen=new Set(),seenOrder=[];let max={{{options.MaximumMessages}}},fade={{{options.FadeOutSeconds * 1000}}},showTime={{{options.ShowTimestamps.ToString().ToLowerInvariant()}}},showBadges={{{options.ShowBadges.ToString().ToLowerInvariant()}}},showEmotes={{{options.ShowEmotes.ToString().ToLowerInvariant()}}};
function span(c,t){const e=document.createElement('span');e.className=c;e.textContent=t;return e}
function apply(s){const torn=(s.messageTheme||'').toLowerCase()==='tornblack';max=s.maximumMessages;fade=s.fadeOutMilliseconds;showTime=s.showTimestamps;showBadges=s.showBadges;showEmotes=s.showEmotes;const d=document.documentElement.style;d.setProperty('--align',s.alignment);d.setProperty('--font-size',s.fontSize+'px');d.setProperty('--message-padding',torn?'0':'4px 8px');d.setProperty('--message-border-width',torn?'14px 30px 16px':'0');d.setProperty('--message-border-image',torn?"url('/overlay/assets/message-torn-black.png')":'none');d.setProperty('--message-left-fill',torn?'linear-gradient(#050609,#050609)':'none');d.setProperty('--message-left-fill-size',torn?'38px calc(100% - 18px)':'0 0');d.setProperty('--message-radius',torn?'0':'8px');d.setProperty('--background-opacity',s.darkBackground&&!torn?s.backgroundOpacity:'0');d.setProperty('--text-shadow',s.textShadow?'0 2px 3px #000,0 0 5px #000':'none');d.setProperty('--text-outline',s.textOutline?'0.35px rgba(0,0,0,.85)':'0 transparent');while(root.children.length>max)root.firstChild.remove()}
async function settings(){try{apply(await(await fetch('/overlay/settings',{cache:'no-store'})).json())}catch{}}settings();setInterval(settings,2000);
new EventSource('/overlay/events').onmessage=e=>{const m=JSON.parse(e.data);if(m.type==='clear'){const channel=(m.channel||'').toLowerCase();for(const row of [...root.children])if((row.dataset.channel||'').toLowerCase()===channel)row.remove();return}if(m.id&&seen.has(m.id))return;if(m.id){seen.add(m.id);seenOrder.push(m.id);if(seenOrder.length>1000)seen.delete(seenOrder.shift())}const row=document.createElement('div');row.className='m'+(m.eventKind?' event '+(m.eventPlatform||'').toLowerCase():'');row.dataset.channel=m.channel||'';if(showTime)row.append(span('time',m.time));if(m.eventKind)row.append(span('event-title',(m.eventPlatform||'EVENT')+' · '+m.eventKind.replaceAll('_',' ')));if(m.rewardTitle)row.append(span('badge',m.rewardTitle+(m.rewardCost?' · '+m.rewardCost:'')));if(showBadges)for(const b of m.badges){if(b.image){const i=document.createElement('img');i.className='emote';i.src=b.image;i.alt=b.label;i.title=b.label;row.append(i)}else row.append(span('badge',b.label))}const u=span('user',(m.user||'')+(m.user?':':''));if(/^#[0-9a-f]{6}$/i.test(m.color))u.style.color=m.color;row.append(u);if(showEmotes&&m.parts?.length){let stack=null;for(const p of m.parts){if(p.kind==='emote'&&p.image){if(!p.zeroWidth||!stack){stack=document.createElement('span');stack.className='emote-stack';row.append(stack)}const i=document.createElement('img');i.className='emote';i.src=p.image;i.alt=p.text;i.title=p.provider?p.text+' ('+p.provider+')':p.text;stack.append(i)}else{row.append(document.createTextNode(p.text));if((p.text||'').trim())stack=null} } }else row.append(document.createTextNode(m.text));if(m.eventAmount)row.append(span('event-amount',m.eventAmount));root.append(row);while(root.children.length>max)root.firstChild.remove();if(fade)setTimeout(()=>row.remove(),fade)};
</script></body></html>
""";

    private sealed record OverlayHistoryEntry(string Channel, string Payload);
}
