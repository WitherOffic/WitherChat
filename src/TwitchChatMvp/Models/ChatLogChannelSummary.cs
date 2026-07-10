namespace TwitchChatMvp.Models;

public sealed class ChatLogChannelSummary
{
    public string Login { get; init; } = string.Empty;
    public string DirectoryPath { get; init; } = string.Empty;

    public override string ToString() => Login;
}
