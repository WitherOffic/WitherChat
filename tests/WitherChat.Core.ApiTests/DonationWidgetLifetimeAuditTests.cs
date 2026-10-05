using System.Net;
using System.Net.WebSockets;
using System.Text;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class DonationWidgetLifetimeAuditTests
{
    private const string WidgetToken = "WidgetToken_1234567890";

    [Fact]
    public async Task SocketCreatedByAnInFlightHandshakeAfterDisposeIsNotPublished()
    {
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new ScriptedSocket();
        using var client = new DonationAlertsWidgetSocketClient(new WidgetHandler(), (_, _) =>
        {
            factoryStarted.TrySetResult();
            return releaseFactory.Task;
        });
        var prepare = client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        client.Dispose();
        releaseFactory.TrySetResult(socket);

        var error = await Record.ExceptionAsync(() => prepare);
        Assert.True(error is ObjectDisposedException or OperationCanceledException);
        Assert.False(client.IsConnected);
        Assert.True(socket.WasDisposed);
    }

    [Fact]
    public async Task DisposeInsideReadyNotificationSuppressesBufferedPlaybackEvents()
    {
        var socket = new ScriptedSocket(bufferPlayback: true);
        using var client = new DonationAlertsWidgetSocketClient(new WidgetHandler(), (_, _) =>
            Task.FromResult<WebSocket>(socket));
        var started = 0;
        client.PlaybackChanged += (_, args) =>
        {
            if (args.Action == DonationAlertsPlaybackAction.ConnectionRestored)
            {
                client.Dispose();
            }
            else if (args.Action == DonationAlertsPlaybackAction.Started)
            {
                Interlocked.Increment(ref started);
            }
        };

        _ = await Record.ExceptionAsync(() =>
            client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken));

        Assert.False(client.IsConnected);
        Assert.False(client.IsAlertPlaying);
        Assert.Equal(0, Volatile.Read(ref started));
        Assert.True(socket.WasDisposed);
        client.Dispose();
    }

    [Fact]
    public void PacketsThatArriveAfterDisposeCannotRestorePlaybackState()
    {
        using var client = new DonationAlertsWidgetSocketClient(new WidgetHandler());
        var events = 0;
        client.PlaybackChanged += (_, _) => Interlocked.Increment(ref events);
        client.Dispose();
        foreach (var packet in PlaybackPackets())
        {
            client.ProcessSocketPacket(packet);
        }

        Assert.False(client.IsAlertPlaying);
        Assert.Equal(0, client.ActiveAlertId);
        Assert.Equal(0, Volatile.Read(ref events));
    }

    [Fact]
    public async Task QueuedPrepareIsReleasedAndRejectedAfterDisposeWithoutNewNetworkWork()
    {
        var factoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new WidgetHandler();
        var socket = new ScriptedSocket();
        using var client = new DonationAlertsWidgetSocketClient(handler, (_, _) =>
        {
            factoryStarted.TrySetResult();
            return releaseFactory.Task;
        });
        var first = client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        await factoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var second = client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        client.Dispose();
        releaseFactory.TrySetResult(socket);
        var firstError = Record.ExceptionAsync(() => first).AsTask();
        var secondError = Record.ExceptionAsync(() => second).AsTask();
        var errors = await Task.WhenAll(firstError, secondError)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.All(errors, error => Assert.True(error is ObjectDisposedException or OperationCanceledException));
        Assert.Equal(1, handler.RequestCount);
        Assert.False(client.IsConnected);
        Assert.True(socket.WasDisposed);
    }

    [Fact]
    public void DisposeInsideStartedNotificationClearsTheActiveAlertAndIsRepeatable()
    {
        using var client = new DonationAlertsWidgetSocketClient(new WidgetHandler());
        client.PlaybackChanged += (_, args) =>
        {
            if (args.Action == DonationAlertsPlaybackAction.Started)
            {
                client.Dispose();
            }
        };
        foreach (var packet in PlaybackPackets())
        {
            client.ProcessSocketPacket(packet);
        }

        Assert.False(client.IsAlertPlaying);
        Assert.Equal(0, client.ActiveAlertId);
        client.Dispose();
    }

    [Fact]
    public async Task PeerClosedSocketIsDisposedBeforeReportingConnectionLost()
    {
        var socket = new ScriptedSocket(closeAfterReady: true);
        using var client = new DonationAlertsWidgetSocketClient(new WidgetHandler(), (_, _) =>
            Task.FromResult<WebSocket>(socket));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PlaybackChanged += (_, args) =>
        {
            if (args.Action == DonationAlertsPlaybackAction.ConnectionLost)
            {
                lost.TrySetResult();
            }
        };

        await client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.False(client.IsConnected);
        Assert.True(socket.WasDisposed);
    }

    [Fact]
    public async Task CancellingAJoinedStartCannotRemoveTheOriginalCommandConfirmation()
    {
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var joinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new WidgetHandler();
        var socket = new ScriptedSocket();
        using var client = new DonationAlertsWidgetSocketClient(handler, (_, _) =>
            Task.FromResult<WebSocket>(socket));
        await client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        var owner = client.StartAlertAsync(WidgetToken, 42, ownerCancellation.Token);
        var joined = client.StartAlertAsync(WidgetToken, 42, joinedCancellation.Token);
        try
        {
            Assert.False(owner.IsCompleted);
            Assert.False(joined.IsCompleted);
            await joinedCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
            foreach (var packet in PlaybackPackets()) client.ProcessSocketPacket(packet);

            await owner.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(42, client.ActiveAlertId);
        }
        finally
        {
            await ownerCancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => owner);
        }
    }

    [Fact]
    public async Task JoinedStartsShareOneNetworkCommandAndBothReceiveConfirmation()
    {
        var handler = new WidgetHandler();
        var socket = new ScriptedSocket();
        using var client = new DonationAlertsWidgetSocketClient(handler, (_, _) =>
            Task.FromResult<WebSocket>(socket));
        await client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        var owner = client.StartAlertAsync(WidgetToken, 42, TestContext.Current.CancellationToken);
        var joined = client.StartAlertAsync(WidgetToken, 42, TestContext.Current.CancellationToken);
        foreach (var packet in PlaybackPackets()) client.ProcessSocketPacket(packet);

        await Task.WhenAll(owner, joined).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CancellingTheCommandOwnerAllowsANewStartToBeSent()
    {
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new WidgetHandler();
        var socket = new ScriptedSocket();
        using var client = new DonationAlertsWidgetSocketClient(handler, (_, _) =>
            Task.FromResult<WebSocket>(socket));
        await client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        var owner = client.StartAlertAsync(WidgetToken, 42, ownerCancellation.Token);
        await ownerCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);

        var retry = client.StartAlertAsync(WidgetToken, 42, TestContext.Current.CancellationToken);
        foreach (var packet in PlaybackPackets()) client.ProcessSocketPacket(packet);
        await retry.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task CancellingAnAcceptedStartOwnerCannotRemoveTheJoinedConfirmation()
    {
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new WidgetHandler();
        var socket = new ScriptedSocket();
        using var client = new DonationAlertsWidgetSocketClient(handler, (_, _) =>
            Task.FromResult<WebSocket>(socket));
        await client.PrepareAsync(WidgetToken, TestContext.Current.CancellationToken);
        var owner = client.StartAlertAsync(WidgetToken, 42, ownerCancellation.Token);
        var joined = client.StartAlertAsync(WidgetToken, 42, TestContext.Current.CancellationToken);
        await ownerCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        foreach (var packet in PlaybackPackets()) client.ProcessSocketPacket(packet);

        await joined.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(42, client.ActiveAlertId);
    }

    private static string[] PlaybackPackets() =>
    [
        "42[\"donation\",\"{\\\"id\\\":42,\\\"username\\\":\\\"Viewer\\\",\\\"amount\\\":100,\\\"currency\\\":\\\"RUB\\\",\\\"message\\\":\\\"Test\\\"}\"]",
        "42[\"alert-show\",{\"action\":\"start\",\"alert_id\":42,\"alert_type\":1}]"
    ];

    private sealed class WidgetHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/repeatalert"
                    ? "{\"status\":\"success\"}"
                    : "<script>new SocketIOClientProxy({ socketHost: 'wss://socket4.donationalerts.com:443' })</script>")
            });
        }
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Queue<string> _packets;
        private readonly bool _closeAfterReady;
        private WebSocketState _state = WebSocketState.Open;

        public ScriptedSocket(bool bufferPlayback = false, bool closeAfterReady = false)
        {
            _closeAfterReady = closeAfterReady;
            _packets = new Queue<string>(
            [
                "0{\"sid\":\"socket-id\",\"pingInterval\":25000,\"pingTimeout\":60000}",
                "40",
                .. (bufferPlayback ? PlaybackPackets() : []),
                "3"
            ]);
        }

        public bool WasDisposed { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { WasDisposed = true; _state = WebSocketState.Closed; }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_packets.TryDequeue(out var packet))
            {
                var bytes = Encoding.UTF8.GetBytes(packet);
                bytes.CopyTo(buffer.Array!, buffer.Offset);
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }
            if (_closeAfterReady)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
