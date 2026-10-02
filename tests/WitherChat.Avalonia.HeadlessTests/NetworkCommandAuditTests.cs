using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.Models;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, true)]
    [InlineData(280, 340, false)]
    [InlineData(1100, 760, true)]
    [InlineData(1100, 760, false)]
    public async Task FailedChannelRemovalKeepsRowAndShowsReadableRetryError(int width, int height, bool selectedCommand)
    {
        var client = new NetworkAuditClient { FailPart = true };
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        var channel = new ChannelSessionViewModel("audit");
        vm.SavedChannels.Clear();
        vm.SavedChannels.Add(channel);
        vm.SelectedSavedChannel = channel;
        ConfigureAuxiliaryAuditWindow(fixture, width, height, "ru", false);
        fixture.Window.Show();
        vm.IsChannelEditorOpen = true;
        if (selectedCommand) await vm.RemoveChannelCommand.ExecuteAsync(null);
        else await vm.RemoveSavedChannelCommand.ExecuteAsync(channel);
        Assert.Same(channel, Assert.Single(vm.SavedChannels));
        Assert.Same(channel, vm.SelectedSavedChannel);
        Assert.NotEmpty(vm.StatusDetail);
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var error = fixture.Window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(text => text.IsEffectivelyVisible && text.Text == vm.StatusDetail);
        AssertAuxiliaryAuditInside(error, fixture.Window, width, height);
        using var frame = fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!, $"channel-remove-error-{selectedCommand}-{width}x{height}.png");
        client.FailPart = false;
        await vm.RemoveSavedChannelCommand.ExecuteAsync(channel);
        Assert.Empty(vm.SavedChannels);
        Assert.Empty(vm.Channel);
    }

    [AvaloniaFact]
    public async Task ClosingDuringConnectionCancelsJoinAndDoesNotAddLateChannel()
    {
        var client = new NetworkAuditClient(joined: false) { BlockJoin = true };
        await using var fixture = new WindowFixture(chatClient: client);
        var vm = fixture.ViewModel;
        vm.SavedChannels.Clear();
        vm.Channel = "audit";
        var connect = vm.ConnectCommand.ExecuteAsync(null);
        try
        {
            await client.JoinStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(client.JoinToken.CanBeCanceled);
            await vm.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await connect.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(client.JoinToken.IsCancellationRequested);
            Assert.Empty(vm.SavedChannels);
        }
        finally
        {
            client.Release.TrySetResult();
            await connect;
        }
    }

    [AvaloniaFact]
    public async Task QueuedConnectionStatusCannotChangeUiAfterShutdownStarts()
    {
        var client = new NetworkAuditClient();
        await using var fixture = new WindowFixture(chatClient: client);
        fixture.Window.Show();
        var vm = fixture.ViewModel;
        vm.ConnectionState = ChatConnectionState.Reconnecting;
        vm.StatusDetail = "closing";
        client.PublishStatus(ChatConnectionState.Connected);
        await vm.DisposeAsync();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        Assert.Equal(ChatConnectionState.Reconnecting, vm.ConnectionState);
        Assert.Equal("closing", vm.StatusDetail);
    }

    private sealed class NetworkAuditClient(bool joined = true) : IWitherChatClient
    {
        private readonly HashSet<string> _channels = joined ? ["audit"] : [];
        public bool FailPart { get; set; }
        public bool BlockJoin { get; set; }
        public bool BlockPart { get; set; }
        public TaskCompletionSource PartStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken JoinToken { get; private set; }
        public TaskCompletionSource JoinStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<ChatMessageEventArgs>? MessageReceived;
        public event EventHandler<ChatConnectionStatusEventArgs>? StatusChanged;
        public string CurrentChannel => _channels.FirstOrDefault() ?? string.Empty;
        public IReadOnlyCollection<string> Channels => _channels.ToArray();
        public bool IsConnected => _channels.Count > 0;
        public void Publish(ChatMessage message) => MessageReceived?.Invoke(this, new ChatMessageEventArgs(message));
        public void PublishStatus(ChatConnectionState state) =>
            StatusChanged?.Invoke(this, new ChatConnectionStatusEventArgs(state, CurrentChannel));
        public Task ConnectAsync(string channel, CancellationToken cancellationToken = default) =>
            JoinChannelAsync(channel, cancellationToken);
        public async Task JoinChannelAsync(string channel, CancellationToken cancellationToken = default)
        {
            JoinToken = cancellationToken;
            JoinStarted.TrySetResult();
            if (BlockJoin) await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _channels.Add(channel);
            PublishStatus(ChatConnectionState.Connected);
        }
        public async Task PartChannelAsync(string channel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PartStarted.TrySetResult();
            if (BlockPart) await Release.Task.WaitAsync(cancellationToken);
            if (FailPart) throw new IOException("Simulated network failure.");
            _channels.Remove(channel);
        }
        public Task DisconnectAsync()
        {
            _channels.Clear();
            PublishStatus(ChatConnectionState.Disconnected);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
