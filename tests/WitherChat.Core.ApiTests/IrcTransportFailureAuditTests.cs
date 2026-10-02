using System.Net.Sockets;
using System.Reflection;
using System.Text;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class IrcTransportFailureAuditTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedMembershipWriteRetainsPreviousChannelState(bool joining)
    {
        await using var client = new TwitchIrcClient();
        using var stream = new FaultingIrcStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true, NewLine = "\r\n" };
        Seed(client, writer);
        stream.Fail = true;
        try
        {
            var error = await Record.ExceptionAsync(() => joining
                ? client.JoinChannelAsync("beta", TestContext.Current.CancellationToken)
                : client.PartChannelAsync("alpha", TestContext.Current.CancellationToken));
            Assert.IsAssignableFrom<IOException>(error);
            Assert.Equal("alpha", client.CurrentChannel);
            Assert.Equal(new[] { "alpha" }, client.Channels);
            Assert.False(client.IsConnected);
            Assert.Null(GetField(client, "_writer"));
        }
        finally { stream.Fail = false; }
    }

    [Fact]
    public async Task DisconnectCompletesEvenWhenWriterFlushFails()
    {
        await using var client = new TwitchIrcClient();
        using var stream = new FaultingIrcStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
        Seed(client, writer);
        using var tcp = new TcpClient();
        SetField(client, "_tcpClient", tcp);
        writer.Write("unsent buffer");
        stream.Fail = true;
        try
        {
            await client.DisconnectAsync();
            Assert.False(client.IsConnected);
            Assert.Empty(client.Channels);
            Assert.Empty(client.CurrentChannel);
            Assert.Null(GetField(client, "_writer"));
            Assert.Null(GetField(client, "_tcpClient"));
            Assert.Throws<ObjectDisposedException>(() => tcp.GetStream());
        }
        finally { stream.Fail = false; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulMembershipWritesCommitExpectedState(bool joining)
    {
        await using var client = new TwitchIrcClient();
        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true, NewLine = "\r\n" };
        Seed(client, writer);
        if (joining)
        {
            await client.JoinChannelAsync("beta", TestContext.Current.CancellationToken);
            Assert.Equal("beta", client.CurrentChannel);
            Assert.Contains("alpha", client.Channels);
            Assert.Contains("beta", client.Channels);
            Assert.Equal("JOIN #beta\r\n", Encoding.UTF8.GetString(stream.ToArray()));
        }
        else
        {
            await client.PartChannelAsync("alpha", TestContext.Current.CancellationToken);
            Assert.Empty(client.CurrentChannel);
            Assert.Empty(client.Channels);
            Assert.False(client.IsConnected);
            Assert.Equal("PART #alpha\r\n", Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    private static void Seed(TwitchIrcClient client, StreamWriter writer)
    {
        SetField(client, "_writer", writer);
        SetField(client, "_runTask", Task.CompletedTask); // No network connection is made.
        SetField(client, "_isConnected", true);
        SetField(client, "_currentChannel", "alpha");
        ((HashSet<string>)GetField(client, "_channels")!).Add("alpha");
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class FaultingIrcStream : MemoryStream
    {
        public bool Fail { get; set; }
        public override void Flush()
        {
            if (Fail) throw new IOException("Simulated broken IRC transport.");
            base.Flush();
        }
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Fail ? Task.FromException(new IOException("Simulated broken IRC transport.")) : base.FlushAsync(cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Fail ? ValueTask.FromException(new IOException("Simulated broken IRC transport.")) : base.WriteAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Fail) throw new IOException("Simulated broken IRC transport.");
            base.Write(buffer, offset, count);
        }
    }
}
