using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed partial class TwitchIrcClient : IWitherChatClient
{
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;
    private const int MaximumLineLength = 64 * 1024;
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20)
    ];

    private readonly Func<string, TaskCompletionSource, CancellationToken, Task>? _connectionRunner;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _channelsSync = new();
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private TcpClient? _tcpClient;
    private StreamWriter? _writer;
    private string _currentChannel = string.Empty;
    private bool _isConnected;
    private bool _disposed;

    public TwitchIrcClient() { }

    internal TwitchIrcClient(Func<string, TaskCompletionSource, CancellationToken, Task> connectionRunner) =>
        _connectionRunner = connectionRunner ?? throw new ArgumentNullException(nameof(connectionRunner));

    public event EventHandler<ChatMessageEventArgs>? MessageReceived;
    public event EventHandler<ChatConnectionStatusEventArgs>? StatusChanged;

    public string CurrentChannel => _currentChannel;
    public IReadOnlyCollection<string> Channels
    {
        get
        {
            lock (_channelsSync)
            {
                return _channels.ToArray();
            }
        }
    }
    public bool IsConnected => _isConnected;

    public async Task ConnectAsync(string channel, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedChannel = NormalizeChannel(channel);
        if (!ChannelPattern().IsMatch(normalizedChannel))
        {
            throw new ArgumentException("Twitch channel must contain 1-25 lowercase letters, digits or underscores.", nameof(channel));
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            await StopRunLoopAsync().ConfigureAwait(false);
            _currentChannel = normalizedChannel;
            lock (_channelsSync)
            {
                _channels.Clear();
                _channels.Add(normalizedChannel);
            }
            _runCancellation = new CancellationTokenSource();
            var initialConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _runTask = RunLoopAsync(normalizedChannel, initialConnection, _runCancellation.Token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await initialConnection.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                await StopRunLoopAsync().ConfigureAwait(false);
                _currentChannel = string.Empty;
                lock (_channelsSync) _channels.Clear();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task JoinChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedChannel = NormalizeChannel(channel);
        if (!ChannelPattern().IsMatch(normalizedChannel))
        {
            throw new ArgumentException("Twitch channel must contain 1-25 lowercase letters, digits or underscores.", nameof(channel));
        }

        if (_runTask is null)
        {
            await ConnectAsync(normalizedChannel, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            bool added;
            lock (_channelsSync)
            {
                if (!_isConnected)
                {
                    _channels.Add(normalizedChannel);
                    _currentChannel = normalizedChannel;
                    return;
                }
                added = !_channels.Contains(normalizedChannel);
            }
            if (added && _isConnected)
                await WriteMembershipLineAsync("JOIN #" + normalizedChannel, cancellationToken).ConfigureAwait(false);
            lock (_channelsSync)
            {
                _channels.Add(normalizedChannel);
            }
            _currentChannel = normalizedChannel;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task PartChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var normalizedChannel = NormalizeChannel(channel);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            bool connected;
            lock (_channelsSync)
            {
                if (!_channels.Contains(normalizedChannel)) return;
                connected = _isConnected;
                if (!connected)
                {
                    _channels.Remove(normalizedChannel);
                    _currentChannel = _channels.FirstOrDefault() ?? string.Empty;
                }
            }
            if (connected)
                await WriteMembershipLineAsync("PART #" + normalizedChannel, cancellationToken).ConfigureAwait(false);
            string nextChannel;
            lock (_channelsSync)
            {
                _channels.Remove(normalizedChannel);
                nextChannel = _channels.FirstOrDefault() ?? string.Empty;
            }
            _currentChannel = nextChannel;
            if (nextChannel.Length == 0)
            {
                await StopRunLoopAsync().ConfigureAwait(false);
                SetStatus(ChatConnectionState.Disconnected, normalizedChannel);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopRunLoopAsync().ConfigureAwait(false);
            _currentChannel = string.Empty;
            lock (_channelsSync)
            {
                _channels.Clear();
            }
            SetStatus(ChatConnectionState.Disconnected, string.Empty);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = completion.Task;
                _ = CompleteDisposeAsync(completion);
            }
            return new ValueTask(_disposeTask);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Cleanup exceptions are propagated to every caller through the shared completion task.")]
    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, true);
        await DisconnectAsync().ConfigureAwait(false);
        _writer?.Dispose();
        _tcpClient?.Dispose();
        // Keep managed gates available for already-queued callers to observe
        // the disposed-state guard. No AvailableWaitHandle is allocated.
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The reconnect boundary must convert every transport failure into status or a retry.")]
    private async Task RunLoopAsync(
        string channel,
        TaskCompletionSource initialConnection,
        CancellationToken cancellationToken)
    {
        var reconnectAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(
                reconnectAttempt == 0 ? ChatConnectionState.Connecting : ChatConnectionState.Reconnecting,
                channel);

            try
            {
                await (_connectionRunner is null
                    ? RunConnectionAsync(channel, initialConnection, cancellationToken)
                    : _connectionRunner(channel, initialConnection, cancellationToken)).ConfigureAwait(false);
                reconnectAttempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                CloseTransport();
                if (!initialConnection.Task.IsCompleted)
                {
                    initialConnection.TrySetException(exception);
                    SetStatus(ChatConnectionState.Error, channel, exception.Message);
                    break;
                }

                SetStatus(ChatConnectionState.Reconnecting, channel, exception.Message);
                var delay = ReconnectDelays[Math.Min(reconnectAttempt, ReconnectDelays.Length - 1)];
                reconnectAttempt++;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        CloseTransport();
        if (!_disposed && cancellationToken.IsCancellationRequested)
        {
            SetStatus(ChatConnectionState.Disconnected, channel);
        }
    }

    private async Task RunConnectionAsync(
        string channel,
        TaskCompletionSource initialConnection,
        CancellationToken cancellationToken)
    {
        var tcpClient = new TcpClient { NoDelay = true };
        _tcpClient = tcpClient;
        await tcpClient.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);

        using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = Host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            },
            cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(
            sslStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true);
        using var writer = new StreamWriter(
            sslStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4 * 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n"
        };
        _writer = writer;

        await WriteLineAsync("PASS SCHMOOPIIE", cancellationToken).ConfigureAwait(false);
        await WriteLineAsync(
            "NICK justinfan" + RandomNumberGenerator.GetInt32(10_000, 100_000).ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await WriteLineAsync(
            "CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership",
            cancellationToken).ConfigureAwait(false);
        await RestoreChannelsAsync(WriteLineAsync, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        SetStatus(ChatConnectionState.Connected, CurrentChannel);
        initialConnection.TrySetResult();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IOException("Twitch closed the IRC connection.");
            }

            if (line.Length > MaximumLineLength)
            {
                continue;
            }

            if (line.StartsWith("PING ", StringComparison.Ordinal))
            {
                await WriteLineAsync("PONG " + line[5..], cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (line.Contains(" RECONNECT", StringComparison.Ordinal))
            {
                throw new IOException("Twitch requested reconnection.");
            }

            if (TryParseChatMessage(line, channel, out var message))
            {
                MessageReceived?.Invoke(this, new ChatMessageEventArgs(message));
            }
        }
    }

    internal async Task RestoreChannelsAsync(
        Func<string, CancellationToken, Task> writeLine, CancellationToken cancellationToken)
    {
        var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string[] additions;
            string[] removals;
            lock (_channelsSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                additions = _channels.Except(joined).ToArray();
                removals = joined.Except(_channels).ToArray();
                if (additions.Length == 0 && removals.Length == 0)
                {
                    // Offline membership updates use this same lock, so no
                    // change can slip between the last snapshot and readiness.
                    _isConnected = true;
                    return;
                }
            }
            foreach (var channel in removals)
            {
                await writeLine("PART #" + channel, cancellationToken).ConfigureAwait(false);
                joined.Remove(channel);
            }
            foreach (var channel in additions)
            {
                await writeLine("JOIN #" + channel, cancellationToken).ConfigureAwait(false);
                joined.Add(channel);
            }
        }
    }

    private async Task WriteMembershipLineAsync(string line, CancellationToken cancellationToken)
    {
        try
        {
            await WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // An interrupted command may have been only partly sent. Reconnect
            // using the unchanged desired channel set instead of leaving ghosts.
            CloseTransport();
            throw;
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var writer = _writer ?? throw new IOException("IRC writer is not connected.");
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task StopRunLoopAsync()
    {
        var cancellation = _runCancellation;
        var task = _runTask;
        _runCancellation = null;
        _runTask = null;

        if (cancellation is null)
        {
            CloseTransport();
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        CloseTransport();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cancellation.Dispose();
    }

    private void CloseTransport()
    {
        _isConnected = false;
        var writer = Interlocked.Exchange(ref _writer, null);
        try
        {
            writer?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        var client = Interlocked.Exchange(ref _tcpClient, null);
        try
        {
            client?.Dispose();
        }
        catch (SocketException)
        {
        }
    }

    private void SetStatus(ChatConnectionState state, string channel, string? detail = null)
    {
        _isConnected = state == ChatConnectionState.Connected;
        StatusChanged?.Invoke(this, new ChatConnectionStatusEventArgs(state, channel, detail));
    }

    internal static bool TryParseChatMessage(string line, string fallbackChannel, out ChatMessage message)
    {
        message = null!;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var position = 0;
        if (line[0] == '@')
        {
            var tagsEnd = line.IndexOf(' ', StringComparison.Ordinal);
            if (tagsEnd <= 1)
            {
                return false;
            }

            ParseTags(line.AsSpan(1, tagsEnd - 1), tags);
            position = tagsEnd + 1;
        }

        if (position >= line.Length || line[position] != ':')
        {
            return false;
        }

        var prefixEnd = line.IndexOf(' ', position);
        if (prefixEnd < 0)
        {
            return false;
        }

        var prefix = line.AsSpan(position + 1, prefixEnd - position - 1);
        var commandStart = prefixEnd + 1;
        if (!line.AsSpan(commandStart).StartsWith("PRIVMSG ", StringComparison.Ordinal))
        {
            return false;
        }

        var channelMarker = line.IndexOf('#', commandStart);
        var textMarker = line.IndexOf(" :", commandStart, StringComparison.Ordinal);
        if (channelMarker < 0 || textMarker < 0 || textMarker <= channelMarker)
        {
            return false;
        }

        var channel = line[(channelMarker + 1)..textMarker].Trim();
        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = fallbackChannel;
        }

        var text = line[(textMarker + 2)..];
        var isAction = text.Length > 9 &&
                       text.StartsWith("\u0001ACTION ", StringComparison.Ordinal) &&
                       text.EndsWith('\u0001');
        if (isAction)
        {
            text = text[8..^1];
        }

        var bangIndex = prefix.IndexOf('!');
        var login = bangIndex > 0 ? prefix[..bangIndex].ToString() : prefix.ToString();
        var displayName = GetTag(tags, "display-name");
        var id = GetTag(tags, "id");
        var badges = ParseBadges(
            GetTag(tags, "badges"),
            GetTag(tags, "badge-info"),
            GetTag(tags, "source-badges"),
            GetTag(tags, "source-badge-info"));
        EnsureRoleBadge(badges, "moderator", GetTag(tags, "mod") == "1");
        EnsureRoleBadge(
            badges,
            "subscriber",
            GetTag(tags, "subscriber") == "1" &&
            badges.All(badge => !string.Equals(badge.SetId, "founder", StringComparison.OrdinalIgnoreCase)));
        EnsureRoleBadge(badges, "vip", GetTag(tags, "vip") == "1");
        EnsureRoleBadge(badges, "turbo", GetTag(tags, "turbo") == "1");
        var userType = GetTag(tags, "user-type");
        if (userType is "admin" or "global_mod" or "staff")
        {
            EnsureRoleBadge(badges, userType, enabled: true);
        }

        message = new ChatMessage
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            Channel = channel,
            BroadcasterId = GetTag(tags, "room-id"),
            UserId = GetTag(tags, "user-id"),
            UserLogin = login,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? login : displayName,
            Text = text,
            Timestamp = ParseTimestamp(GetTag(tags, "tmi-sent-ts")),
            UserColor = GetTag(tags, "color"),
            Badges = badges,
            Parts = TwitchEmoteParser.Parse(text, GetTag(tags, "emotes")),
            ReplyParentDisplayName = GetTag(tags, "reply-parent-display-name"),
            ReplyParentText = GetTag(tags, "reply-parent-msg-body"),
            IsAction = isAction,
            IsPinned = tags.ContainsKey("pinned-chat-paid-amount"),
            CustomRewardId = GetTag(tags, "custom-reward-id")
        };
        return true;
    }

    private static void ParseTags(ReadOnlySpan<char> value, Dictionary<string, string> destination)
    {
        foreach (var pair in value.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            var tagValue = separator < 0 ? string.Empty : UnescapeTag(pair[(separator + 1)..]);
            destination[key] = tagValue;
        }
    }

    private static string UnescapeTag(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var escaped = false;
        foreach (var character in value)
        {
            if (!escaped && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (escaped)
            {
                builder.Append(character switch
                {
                    's' => ' ',
                    ':' => ';',
                    'r' => '\r',
                    'n' => '\n',
                    '\\' => '\\',
                    _ => character
                });
                escaped = false;
                continue;
            }

            builder.Append(character);
        }

        if (escaped)
        {
            builder.Append('\\');
        }

        return builder.ToString();
    }

    private static List<ChatBadge> ParseBadges(
        string value,
        string badgeInfo,
        string sourceValue,
        string sourceBadgeInfo)
    {
        var result = new List<ChatBadge>();
        var sets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddBadges(sourceValue, sourceBadgeInfo, result, sets);
        AddBadges(value, badgeInfo, result, sets);
        return result;
    }

    private static void AddBadges(
        string value,
        string badgeInfo,
        List<ChatBadge> destination,
        HashSet<string> sets)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var infoBySet = ParseBadgeInfo(badgeInfo);
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('/', StringComparison.Ordinal);
            var setId = separator < 0 ? item : item[..separator];
            var versionId = separator < 0 ? string.Empty : item[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(setId) || !sets.Add(setId))
            {
                continue;
            }

            infoBySet.TryGetValue(setId, out var info);
            destination.Add(new ChatBadge(setId, versionId, info ?? string.Empty));
        }
    }

    private static void EnsureRoleBadge(List<ChatBadge> badges, string setId, bool enabled)
    {
        if (!enabled || badges.Any(badge => string.Equals(
                badge.SetId,
                setId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        badges.Add(new ChatBadge(setId, "1"));
    }

    private static Dictionary<string, string> ParseBadgeInfo(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('/', StringComparison.Ordinal);
            if (separator > 0)
            {
                result[item[..separator]] = item[(separator + 1)..];
            }
        }

        return result;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return DateTimeOffset.Now;
    }

    private static string GetTag(Dictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out var value) ? value : string.Empty;

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Twitch IRC channel logins are canonically lowercase.")]
    private static string NormalizeChannel(string channel) =>
        (channel ?? string.Empty).Trim().TrimStart('#').ToLowerInvariant();

    [GeneratedRegex("^[a-z0-9_]{1,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
}
