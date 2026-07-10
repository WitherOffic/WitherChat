using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class ReadOnlyChatClient : IAsyncDisposable
{
    private static readonly Regex ChannelLoginPattern = new("^[a-z0-9_]{1,25}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;

    private readonly FileLogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private TcpClient? _tcpClient;
    private string _channelLogin = string.Empty;
    private string _resolvedRoomId = string.Empty;
    private TaskCompletionSource<bool>? _initialConnection;
    private bool _connectedReported;

    public ReadOnlyChatClient(FileLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<ChatMessageModel>? MessageReceived;
    public event EventHandler<string>? StatusChanged;
    public event Action<string, string>? ChannelIdentityResolved;

    public async Task StartAsync(string channelLogin, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        _channelLogin = NormalizeChannel(channelLogin);
        if (!ChannelLoginPattern.IsMatch(_channelLogin))
        {
            throw new InvalidOperationException("Twitch channel name is invalid.");
        }

        _connectedReported = false;
        _resolvedRoomId = string.Empty;
        _initialConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _logger.Info($"Starting read-only IRC chat connect: channel={_channelLogin}");
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
        TcpClient? tcpClient;

        lock (_gate)
        {
            cts = _runCts;
            task = _runTask;
            tcpClient = _tcpClient;
            _runCts = null;
            _runTask = null;
            _tcpClient = null;
        }

        cts?.Cancel();
        tcpClient?.Close();
        tcpClient?.Dispose();

        if (task is not null)
        {
            try
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            }
            catch
            {
                // Stop is best-effort; the next Start creates a fresh connection.
            }
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                RaiseStatus(attempt == 0 ? "read-only chat connecting..." : "read-only chat reconnecting...");
                await ConnectAndListenAsync(cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Read-only IRC chat connection failed", ex);
                if (!_connectedReported)
                {
                    _initialConnection?.TrySetException(ex);
                    RaiseStatus("read-only chat error: " + ex.Message);
                }
                else
                {
                    RaiseStatus("read-only chat disconnected, reconnecting...");
                }

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

        RaiseStatus("read-only chat disconnected");
    }

    private async Task ConnectAndListenAsync(CancellationToken cancellationToken)
    {
        using var tcpClient = new TcpClient();
        lock (_gate)
        {
            _tcpClient = tcpClient;
        }

        await tcpClient.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);
        await using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsClientAsync(Host).WaitAsync(cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(sslStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(sslStream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        var nick = "justinfan" + Random.Shared.Next(10000, 999999);
        await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands").WaitAsync(cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("PASS SCHMOOPIIE").WaitAsync(cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("NICK " + nick).WaitAsync(cancellationToken).ConfigureAwait(false);
        await writer.WriteLineAsync("JOIN #" + _channelLogin).WaitAsync(cancellationToken).ConfigureAwait(false);

        MarkConnected();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
            {
                var payload = line.Length > 5 ? line[5..] : ":tmi.twitch.tv";
                await writer.WriteLineAsync("PONG " + payload).WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var roomId = GetLineTag(line, "room-id");
            if (string.IsNullOrWhiteSpace(_resolvedRoomId) &&
                ulong.TryParse(roomId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                _resolvedRoomId = roomId;
                ChannelIdentityResolved?.Invoke(_channelLogin, roomId);
            }

            var message = TryParsePrivMessage(line);
            if (message is not null)
            {
                MessageReceived?.Invoke(this, message);
            }
        }
    }

    private static string GetLineTag(string line, string tagName)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '@')
        {
            return string.Empty;
        }

        var tagEnd = line.IndexOf(' ');
        if (tagEnd <= 1)
        {
            return string.Empty;
        }

        foreach (var pair in line.AsSpan(1, tagEnd - 1).ToString().Split(';'))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && pair.AsSpan(0, equals).Equals(tagName, StringComparison.OrdinalIgnoreCase))
            {
                return pair[(equals + 1)..];
            }
        }

        return string.Empty;
    }

    private void MarkConnected()
    {
        if (_connectedReported)
        {
            return;
        }

        _connectedReported = true;
        _initialConnection?.TrySetResult(true);
        _logger.Info("Read-only IRC chat connected.");
        RaiseStatus("read-only chat connected");
    }

    private ChatMessageModel? TryParsePrivMessage(string line)
    {
        if (!line.Contains(" PRIVMSG ", StringComparison.Ordinal))
        {
            return null;
        }

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = line;
        if (rest.StartsWith('@'))
        {
            var tagEnd = rest.IndexOf(' ');
            if (tagEnd <= 1)
            {
                return null;
            }

            foreach (var pair in rest[1..tagEnd].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals < 0)
                {
                    tags[pair] = string.Empty;
                }
                else
                {
                    tags[pair[..equals]] = UnescapeTag(pair[(equals + 1)..]);
                }
            }

            rest = rest[(tagEnd + 1)..];
        }

        var textMarker = " :";
        var textIndex = rest.IndexOf(textMarker, StringComparison.Ordinal);
        if (textIndex < 0)
        {
            return null;
        }

        var commandPart = rest[..textIndex];
        var text = rest[(textIndex + textMarker.Length)..];
        var prefixLogin = GetPrefixLogin(commandPart);
        var login = GetTag(tags, "login");
        if (string.IsNullOrWhiteSpace(login))
        {
            login = prefixLogin;
        }

        if (string.IsNullOrWhiteSpace(login))
        {
            login = GetTag(tags, "display-name").ToLowerInvariant();
        }

        var displayName = GetTag(tags, "display-name");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = login;
        }

        var message = new ChatMessageModel
        {
            Id = string.IsNullOrWhiteSpace(GetTag(tags, "id")) ? "irc-" + Guid.NewGuid().ToString("N") : GetTag(tags, "id"),
            Timestamp = ParseTimestamp(GetTag(tags, "tmi-sent-ts")),
            UserId = GetTag(tags, "user-id"),
            Login = login,
            DisplayName = displayName,
            Text = text,
            Color = GetTag(tags, "color"),
            Badges = ParseBadges(GetTag(tags, "badges"), GetTag(tags, "badge-info")),
            Parts = ParseMessageParts(text, GetTag(tags, "emotes"))
        };

        return message;
    }

    private static ObservableCollection<BadgeModel> ParseBadges(string badgesTag, string badgeInfoTag)
    {
        var badges = new ObservableCollection<BadgeModel>();
        if (string.IsNullOrWhiteSpace(badgesTag))
        {
            return badges;
        }

        var infoBySet = ParseBadgeInfo(badgeInfoTag);
        foreach (var badge in badgesTag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = badge.IndexOf('/');
            var setId = slash >= 0 ? badge[..slash] : badge;
            var id = slash >= 0 ? badge[(slash + 1)..] : string.Empty;
            infoBySet.TryGetValue(setId, out var info);
            badges.Add(new BadgeModel
            {
                SetId = setId,
                Id = id,
                Info = info ?? string.Empty
            });
        }

        return badges;
    }

    private static Dictionary<string, string> ParseBadgeInfo(string badgeInfoTag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(badgeInfoTag))
        {
            return result;
        }

        foreach (var item in badgeInfoTag.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = item.IndexOf('/');
            if (slash > 0)
            {
                result[item[..slash]] = item[(slash + 1)..];
            }
        }

        return result;
    }

    private static ObservableCollection<ChatMessagePartModel> ParseMessageParts(string text, string emotesTag)
    {
        var parts = new ObservableCollection<ChatMessagePartModel>();
        var ranges = ParseEmoteRanges(emotesTag, text.Length);
        var index = 0;

        foreach (var range in ranges)
        {
            if (range.Start > index)
            {
                parts.Add(ChatMessagePartModel.TextPart(text[index..range.Start]));
            }

            var length = range.End - range.Start + 1;
            if (range.Start >= 0 && length > 0 && range.Start + length <= text.Length)
            {
                var emoteText = text.Substring(range.Start, length);
                parts.Add(ChatMessagePartModel.TwitchEmote(emoteText, range.EmoteId));
                index = range.End + 1;
            }
        }

        if (index < text.Length)
        {
            parts.Add(ChatMessagePartModel.TextPart(text[index..]));
        }

        if (parts.Count == 0 && !string.IsNullOrEmpty(text))
        {
            parts.Add(ChatMessagePartModel.TextPart(text));
        }

        return parts;
    }

    private static List<EmoteRange> ParseEmoteRanges(string emotesTag, int textLength)
    {
        var ranges = new List<EmoteRange>();
        if (string.IsNullOrWhiteSpace(emotesTag))
        {
            return ranges;
        }

        foreach (var emoteEntry in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = emoteEntry.IndexOf(':');
            if (colon <= 0 || colon >= emoteEntry.Length - 1)
            {
                continue;
            }

            var emoteId = emoteEntry[..colon];
            foreach (var rangeText in emoteEntry[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dash = rangeText.IndexOf('-');
                if (dash <= 0 ||
                    !int.TryParse(rangeText[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
                    !int.TryParse(rangeText[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
                    start < 0 ||
                    end < start ||
                    end >= textLength)
                {
                    continue;
                }

                ranges.Add(new EmoteRange(start, end, emoteId));
            }
        }

        return ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToList();
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTimeOffset.UtcNow;
            }
        }

        return DateTimeOffset.UtcNow;
    }

    private static string GetPrefixLogin(string commandPart)
    {
        if (!commandPart.StartsWith(':'))
        {
            return string.Empty;
        }

        var bang = commandPart.IndexOf('!');
        return bang > 1 ? commandPart[1..bang].ToLowerInvariant() : string.Empty;
    }

    private static string GetTag(Dictionary<string, string> tags, string key)
    {
        return tags.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static string UnescapeTag(string value)
    {
        return value
            .Replace(@"\s", " ", StringComparison.Ordinal)
            .Replace(@"\:", ";", StringComparison.Ordinal)
            .Replace(@"\\", @"\", StringComparison.Ordinal)
            .Replace(@"\r", "\r", StringComparison.Ordinal)
            .Replace(@"\n", "\n", StringComparison.Ordinal);
    }

    private static string NormalizeChannel(string channelLogin)
    {
        return (channelLogin ?? string.Empty).Trim().TrimStart('@', '#').ToLowerInvariant();
    }

    private void RaiseStatus(string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private sealed record EmoteRange(int Start, int End, string EmoteId);
}
