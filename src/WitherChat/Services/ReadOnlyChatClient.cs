using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class ReadOnlyChatClient : IAsyncDisposable
{
    private static readonly Regex ChannelLoginPattern = new("^[a-z0-9_]{1,25}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string Host = "irc.chat.twitch.tv";
    private const int Port = 6697;
    private const int MaxIrcLineLength = 64 * 1024;
    private static readonly string[] ChannelTargetMarkers =
    [
        " PRIVMSG #",
        " ROOMSTATE #",
        " USERSTATE #",
        " USERNOTICE #",
        " JOIN #",
        " NOTICE #",
        " CLEARCHAT #",
        " CLEARMSG #"
    ];

    private readonly FileLogger _logger;
    private readonly Func<CancellationToken, Task<TwitchTokenSet?>> _tokenProvider;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _requestedChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _joinedChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<bool>> _joinWaiters = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private TcpClient? _tcpClient;
    private StreamWriter? _writer;
    private readonly HashSet<string> _resolvedRoomIds = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool>? _initialConnection;
    private bool _connectedReported;
    private long _runGeneration;

    public ReadOnlyChatClient(
        FileLogger logger,
        Func<CancellationToken, Task<TwitchTokenSet?>> tokenProvider)
    {
        _logger = logger;
        _tokenProvider = tokenProvider;
    }

    public event EventHandler<ChannelChatMessageEventArgs>? MessageReceived;
    public event EventHandler<ChannelConnectionStatusEventArgs>? ChannelStatusChanged;
    public event EventHandler<ChannelIdentityResolvedEventArgs>? ChannelIdentityResolved;
    public event EventHandler<ChannelUserModeratedEventArgs>? UserModerated;
    public event EventHandler<ChannelMessageDeletedEventArgs>? MessageDeleted;
    public event EventHandler<ChannelChatClearedEventArgs>? ChatCleared;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                EnsureRunLoopStarted(cancellationToken);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task JoinChannelAsync(string channelLogin, CancellationToken cancellationToken = default)
    {
        var login = NormalizeChannel(channelLogin);
        if (!ChannelLoginPattern.IsMatch(login))
        {
            throw new InvalidOperationException(L("TwitchChannelNameRequired"));
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter? writer;
            var added = false;
            lock (_gate)
            {
                if (_requestedChannels.Contains(login))
                {
                    return;
                }

                if (_requestedChannels.Count >= 3)
                {
                    throw new InvalidOperationException(L("ChannelLimitHint"));
                }

                _requestedChannels.Add(login);
                _joinedChannels.Remove(login);
                _joinWaiters[login] = NewJoinWaiter();
                added = true;
                writer = _writer;
                EnsureRunLoopStarted(cancellationToken);
            }

            RaiseChannelStatus(login, ChannelConnectionState.Connecting);
            if (added && writer is not null)
            {
                await SendLineAsync("JOIN #" + login, cancellationToken).ConfigureAwait(false);
                _logger.Info($"IRC JOIN sent: {login}");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<bool> WaitForChannelJoinAsync(
        string channelLogin,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var login = NormalizeChannel(channelLogin);
        Task<bool>? task;
        lock (_gate)
        {
            if (_joinedChannels.Contains(login))
            {
                return true;
            }

            task = _joinWaiters.TryGetValue(login, out var waiter) ? waiter.Task : null;
        }

        if (task is null)
        {
            return false;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            return await task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Warn($"IRC JOIN timeout: {login}");
            RaiseChannelStatus(login, ChannelConnectionState.Error, "Twitch IRC did not confirm the channel join in time.");
            return false;
        }
    }

    public async Task PartChannelAsync(string channelLogin, CancellationToken cancellationToken = default)
    {
        var login = NormalizeChannel(channelLogin);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter? writer;
            bool noChannels;
            lock (_gate)
            {
                if (!_requestedChannels.Remove(login))
                {
                    return;
                }

                _joinedChannels.Remove(login);
                if (_joinWaiters.Remove(login, out var waiter))
                {
                    waiter.TrySetResult(false);
                }
                _resolvedRoomIds.Remove(login);
                writer = _writer;
                noChannels = _requestedChannels.Count == 0;
            }

            if (writer is not null)
            {
                await SendLineAsync("PART #" + login, cancellationToken).ConfigureAwait(false);
                _logger.Info($"IRC PART sent: {login}");
            }

            RaiseChannelStatus(login, ChannelConnectionState.Disconnected);
            if (noChannels)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public IReadOnlyList<string> JoinedChannels
    {
        get
        {
            lock (_gate)
            {
                return _requestedChannels.ToArray();
            }
        }
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
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        TcpClient? tcpClient;

        lock (_gate)
        {
            cts = _runCts;
            task = _runTask;
            tcpClient = _tcpClient;
            _runGeneration++;
            _tcpClient = null;
            _writer = null;
            foreach (var waiter in _joinWaiters.Values)
            {
                waiter.TrySetResult(false);
            }
            _requestedChannels.Clear();
            _joinedChannels.Clear();
            _joinWaiters.Clear();
            _resolvedRoomIds.Clear();
        }

        cts?.Cancel();
        tcpClient?.Close();
        tcpClient?.Dispose();

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping the active connection.
            }
            catch (Exception ex)
            {
                _logger.Warn($"IRC connection stop completed with {ex.GetType().Name}.");
            }
        }

        lock (_gate)
        {
            if (ReferenceEquals(_runTask, task))
            {
                _runTask = null;
            }

            if (ReferenceEquals(_runCts, cts))
            {
                _runCts = null;
            }
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
        _sendLock.Dispose();
    }

    private async Task RunLoopAsync(long generation, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested && IsCurrentGeneration(generation))
        {
            try
            {
                if (attempt > 0)
                {
                    _logger.Info("IRC reconnect started.");
                    ResetJoinStateForReconnect(generation);
                }
                await ConnectAndListenAsync(generation, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || !IsCurrentGeneration(generation))
            {
                break;
            }
            catch (Exception ex)
            {
                if (!IsCurrentGeneration(generation))
                {
                    break;
                }

                _logger.Error("Read-only IRC chat connection failed", ex);
                bool connectedReported;
                lock (_gate)
                {
                    connectedReported = _runGeneration == generation && _connectedReported;
                    if (_runGeneration == generation && !connectedReported)
                    {
                        _initialConnection?.TrySetException(ex);
                    }
                }

                if (!connectedReported)
                {
                    FailPendingJoins(generation, ex.Message);
                }
                else
                {
                    RaiseAllRequestedStatuses(generation, ChannelConnectionState.Reconnecting, ex.Message);
                }

                attempt++;
            }
            finally
            {
                lock (_gate)
                {
                    if (_runGeneration == generation)
                    {
                        _writer = null;
                        _tcpClient = null;
                        _resolvedRoomIds.Clear();
                    }
                }
            }

            if (!IsCurrentGeneration(generation))
            {
                break;
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

        RaiseAllRequestedStatuses(generation, ChannelConnectionState.Disconnected);
    }

    private async Task ConnectAndListenAsync(long generation, CancellationToken cancellationToken)
    {
        using var tcpClient = new TcpClient();
        lock (_gate)
        {
            ThrowIfStaleGeneration(generation, cancellationToken);
            _tcpClient = tcpClient;
        }

        await tcpClient.ConnectAsync(Host, Port, cancellationToken).ConfigureAwait(false);
        _logger.Info("IRC socket connected.");
        await using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = Host,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            },
            cancellationToken).ConfigureAwait(false);

        using var reader = new StreamReader(sslStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var lineReader = new BoundedTextLineReader(reader, MaxIrcLineLength);
        await using var writer = new StreamWriter(sslStream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
        lock (_gate)
        {
            ThrowIfStaleGeneration(generation, cancellationToken);
            _writer = writer;
        }

        var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
        var nick = NormalizeChannel(token?.Login ?? string.Empty);
        if (token is null ||
            string.IsNullOrWhiteSpace(token.AccessToken) ||
            !ChannelLoginPattern.IsMatch(nick))
        {
            throw new InvalidOperationException(L("ChatReadRequiresSignIn"));
        }

        if (!token.Scopes.Contains("chat:read", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(L("ChatReadScopeMissing"));
        }

        await SendLineAsync("PASS oauth:" + token.AccessToken, cancellationToken, generation).ConfigureAwait(false);
        await SendLineAsync("NICK " + nick, cancellationToken, generation).ConfigureAwait(false);
        await SendLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands", cancellationToken, generation).ConfigureAwait(false);
        string[] channels;
        lock (_gate)
        {
            ThrowIfStaleGeneration(generation, cancellationToken);
            channels = _requestedChannels.ToArray();
        }

        foreach (var channel in channels)
        {
            await SendLineAsync("JOIN #" + channel, cancellationToken, generation).ConfigureAwait(false);
            _logger.Info($"IRC JOIN sent: {channel}");
        }

        MarkConnected(generation);

        while (!cancellationToken.IsCancellationRequested && IsCurrentGeneration(generation))
        {
            var line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (!IsCurrentGeneration(generation))
            {
                return;
            }

            if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
            {
                var payload = line.Length > 5 ? line[5..] : ":tmi.twitch.tv";
                await SendLineAsync("PONG " + payload, cancellationToken, generation).ConfigureAwait(false);
                continue;
            }

            var roomId = GetLineTag(line, "room-id");
            var channelLogin = GetChannelLogin(line);
            if (!string.IsNullOrWhiteSpace(channelLogin) && IsJoinConfirmationLine(line))
            {
                ConfirmChannelJoined(channelLogin, generation);
            }
            if (!string.IsNullOrWhiteSpace(channelLogin) &&
                ulong.TryParse(roomId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                var notify = false;
                lock (_gate)
                {
                    notify = _runGeneration == generation && _resolvedRoomIds.Add(channelLogin);
                }

                if (notify && IsCurrentGeneration(generation))
                {
                    ChannelIdentityResolved?.Invoke(
                        this,
                        new ChannelIdentityResolvedEventArgs(channelLogin, roomId));
                }
            }

            if (!string.IsNullOrWhiteSpace(channelLogin) && HandleModerationLine(line, channelLogin))
            {
                continue;
            }

            var message = TryParsePrivMessage(line);
            if (message is not null)
            {
                message.ChannelLogin = channelLogin;
                if (!string.IsNullOrWhiteSpace(channelLogin) && IsCurrentGeneration(generation))
                {
                    MessageReceived?.Invoke(this, new ChannelChatMessageEventArgs(channelLogin, message));
                }
            }
        }
    }

    private async Task SendLineAsync(
        string line,
        CancellationToken cancellationToken,
        long? generation = null)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter? writer;
            lock (_gate)
            {
                writer = generation.HasValue && _runGeneration != generation.Value
                    ? null
                    : _writer;
            }

            if (writer is not null)
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private sealed class BoundedTextLineReader(TextReader reader, int maximumLineLength)
    {
        private readonly char[] _buffer = new char[4096];
        private int _offset;
        private int _count;

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            StringBuilder? builder = null;
            while (true)
            {
                if (_offset >= _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        return builder is null ? null : TrimCarriageReturn(builder.ToString());
                    }
                }

                var newlineIndex = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
                var segmentLength = newlineIndex >= 0 ? newlineIndex - _offset : _count - _offset;
                var currentLength = builder?.Length ?? 0;
                if (currentLength + segmentLength > maximumLineLength)
                {
                    throw new InvalidDataException("IRC line exceeded the protocol safety limit.");
                }

                if (builder is null && newlineIndex >= 0)
                {
                    var line = new string(_buffer, _offset, segmentLength);
                    _offset += segmentLength + 1;
                    return TrimCarriageReturn(line);
                }

                builder ??= new StringBuilder(Math.Min(maximumLineLength, 512));
                builder.Append(_buffer, _offset, segmentLength);
                _offset += segmentLength;
                if (newlineIndex >= 0)
                {
                    _offset++;
                    return TrimCarriageReturn(builder.ToString());
                }
            }
        }

        private static string TrimCarriageReturn(string value) =>
            value.EndsWith('\r') ? value[..^1] : value;
    }

    private static string GetChannelLogin(string line)
    {
        foreach (var marker in ChannelTargetMarkers)
        {
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var start = markerIndex + marker.Length;
            var end = line.IndexOf(' ', start);
            return NormalizeChannel(end < 0 ? line[start..] : line[start..end]);
        }

        return string.Empty;
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

    private void MarkConnected(long generation)
    {
        lock (_gate)
        {
            if (_runGeneration != generation || _connectedReported)
            {
                return;
            }

            _connectedReported = true;
            _initialConnection?.TrySetResult(true);
        }

        _logger.Info("Read-only IRC chat connected.");
    }

    private void EnsureRunLoopStarted(CancellationToken cancellationToken)
    {
        if (_runTask is { IsCompleted: false })
        {
            return;
        }

        _runCts?.Dispose();
        _connectedReported = false;
        _initialConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var generation = ++_runGeneration;
        _runCts = cts;
        _runTask = Task.Run(() => RunLoopAsync(generation, cts.Token), CancellationToken.None);
        _logger.Info("IRC connection started.");
    }

    private void ConfirmChannelJoined(string channelLogin, long generation)
    {
        var login = NormalizeChannel(channelLogin);
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            if (_runGeneration != generation ||
                !_requestedChannels.Contains(login) ||
                !_joinedChannels.Add(login))
            {
                return;
            }

            _joinWaiters.TryGetValue(login, out waiter);
        }

        waiter?.TrySetResult(true);
        _logger.Info($"IRC line channel parsed: {login}");
        _logger.Info($"IRC JOIN confirmed: {login}");
        RaiseChannelStatusForGeneration(generation, login, ChannelConnectionState.Connected);
    }

    private void ResetJoinStateForReconnect(long generation)
    {
        string[] channels;
        lock (_gate)
        {
            if (_runGeneration != generation)
            {
                return;
            }

            _joinedChannels.Clear();
            channels = _requestedChannels.ToArray();
            foreach (var channel in channels)
            {
                if (!_joinWaiters.TryGetValue(channel, out var waiter) || waiter.Task.IsCompleted)
                {
                    _joinWaiters[channel] = NewJoinWaiter();
                }
            }
        }

        foreach (var channel in channels)
        {
            RaiseChannelStatusForGeneration(generation, channel, ChannelConnectionState.Reconnecting);
        }
    }

    private void FailPendingJoins(long generation, string error)
    {
        KeyValuePair<string, TaskCompletionSource<bool>>[] waiters;
        lock (_gate)
        {
            if (_runGeneration != generation)
            {
                return;
            }

            waiters = _joinWaiters.ToArray();
        }

        foreach (var (channel, waiter) in waiters)
        {
            waiter.TrySetResult(false);
            RaiseChannelStatusForGeneration(generation, channel, ChannelConnectionState.Error, error);
        }
    }

    private void RaiseAllRequestedStatuses(
        long generation,
        ChannelConnectionState state,
        string error = "")
    {
        string[] channels;
        lock (_gate)
        {
            if (_runGeneration != generation)
            {
                return;
            }

            channels = _requestedChannels.ToArray();
        }

        foreach (var channel in channels)
        {
            RaiseChannelStatusForGeneration(generation, channel, state, error);
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (_gate)
        {
            return _runGeneration == generation;
        }
    }

    private void ThrowIfStaleGeneration(long generation, CancellationToken cancellationToken)
    {
        if (_runGeneration != generation)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void RaiseChannelStatusForGeneration(
        long generation,
        string channelLogin,
        ChannelConnectionState state,
        string error = "")
    {
        if (IsCurrentGeneration(generation))
        {
            RaiseChannelStatus(channelLogin, state, error);
        }
    }

    private static bool IsJoinConfirmationLine(string line) =>
        line.Contains(" ROOMSTATE #", StringComparison.Ordinal) ||
        line.Contains(" USERSTATE #", StringComparison.Ordinal) ||
        line.Contains(" JOIN #", StringComparison.Ordinal) ||
        line.Contains(" PRIVMSG #", StringComparison.Ordinal);

    private static TaskCompletionSource<bool> NewJoinWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        var roomId = GetTag(tags, "room-id");
        var sourceRoomId = GetTag(tags, "source-room-id");
        var hasSourceRoom = !string.IsNullOrWhiteSpace(sourceRoomId) &&
                            !string.Equals(sourceRoomId, roomId, StringComparison.Ordinal);
        var badgesTag = hasSourceRoom ? GetTag(tags, "source-badges") : GetTag(tags, "badges");
        var badgeInfoTag = hasSourceRoom ? GetTag(tags, "source-badge-info") : GetTag(tags, "badge-info");

        var messageId = GetTag(tags, "id");
        if (string.IsNullOrWhiteSpace(messageId))
        {
            messageId = "irc-" + Guid.NewGuid().ToString("N");
        }

        var message = new ChatMessageModel
        {
            Id = messageId,
            MessageId = messageId,
            ReplyParentMessageId = GetTag(tags, "reply-parent-msg-id"),
            ReplyParentUserId = GetTag(tags, "reply-parent-user-id"),
            ReplyParentUserLogin = GetTag(tags, "reply-parent-user-login"),
            ReplyParentDisplayName = GetTag(tags, "reply-parent-display-name"),
            ReplyParentMessageBody = GetTag(tags, "reply-parent-msg-body"),
            Timestamp = ParseTimestamp(GetTag(tags, "tmi-sent-ts")),
            UserId = GetTag(tags, "user-id"),
            RoomId = roomId,
            SourceRoomId = sourceRoomId,
            BadgeBroadcasterId = hasSourceRoom ? sourceRoomId : roomId,
            Login = login,
            DisplayName = displayName,
            Text = text,
            Color = GetTag(tags, "color"),
            Badges = ParseBadges(badgesTag, badgeInfoTag),
            Parts = ParseMessageParts(text, GetTag(tags, "emotes"))
        };

        return message;
    }

    private bool HandleModerationLine(string line, string channelLogin)
    {
        if (line.Contains(" CLEARCHAT #", StringComparison.Ordinal))
        {
            var roomId = GetLineTag(line, "room-id");
            var targetUserId = GetLineTag(line, "target-user-id");
            var targetLogin = GetTrailingParameter(line);
            var observedAt = ParseTimestamp(GetLineTag(line, "tmi-sent-ts"));
            if (string.IsNullOrWhiteSpace(targetUserId) && string.IsNullOrWhiteSpace(targetLogin))
            {
                ChatCleared?.Invoke(this, new ChannelChatClearedEventArgs(channelLogin, roomId, observedAt));
                return true;
            }

            var durationText = GetLineTag(line, "ban-duration");
            var duration = int.TryParse(durationText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : (int?)null;
            UserModerated?.Invoke(this, new ChannelUserModeratedEventArgs(
                channelLogin,
                roomId,
                targetUserId,
                NormalizeChannel(targetLogin),
                duration.HasValue ? PunishmentType.Timeout : PunishmentType.Ban,
                duration,
                observedAt));
            return true;
        }

        if (line.Contains(" CLEARMSG #", StringComparison.Ordinal))
        {
            var targetMessageId = GetLineTag(line, "target-msg-id");
            if (!string.IsNullOrWhiteSpace(targetMessageId))
            {
                MessageDeleted?.Invoke(this, new ChannelMessageDeletedEventArgs(
                    channelLogin,
                    GetLineTag(line, "room-id"),
                    targetMessageId,
                    NormalizeChannel(GetLineTag(line, "login")),
                    ParseTimestamp(GetLineTag(line, "tmi-sent-ts"))));
            }
            return true;
        }

        return false;
    }

    private static string GetTrailingParameter(string line)
    {
        var marker = line.LastIndexOf(" :", StringComparison.Ordinal);
        return marker < 0 || marker + 2 >= line.Length ? string.Empty : line[(marker + 2)..].Trim();
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

    private void RaiseChannelStatus(
        string channelLogin,
        ChannelConnectionState state,
        string error = "")
    {
        ChannelStatusChanged?.Invoke(
            this,
            new ChannelConnectionStatusEventArgs(channelLogin, state, error));
    }

    private static string L(string key) =>
        LocalizationService.Get(LocalizationService.CurrentLanguage, key);

    private sealed record EmoteRange(int Start, int End, string EmoteId);
}
