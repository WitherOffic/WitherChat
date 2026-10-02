using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public interface IWitherChatClient : IAsyncDisposable
{
    event EventHandler<ChatMessageEventArgs>? MessageReceived;
    event EventHandler<ChatConnectionStatusEventArgs>? StatusChanged;

    string CurrentChannel { get; }
    IReadOnlyCollection<string> Channels { get; }
    bool IsConnected { get; }

    Task ConnectAsync(string channel, CancellationToken cancellationToken = default);
    Task JoinChannelAsync(string channel, CancellationToken cancellationToken = default);
    Task PartChannelAsync(string channel, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}
