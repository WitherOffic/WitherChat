using System.Globalization;
using System.IO;

namespace TwitchChatMvp.Models;

public sealed class ChatLogSessionSummary
{
    public string DirectoryPath { get; init; } = string.Empty;
    public ChatLogSessionMetadata Metadata { get; init; } = new();

    public string DisplayTitle => string.IsNullOrWhiteSpace(Metadata.StreamTitle)
        ? Path.GetFileName(DirectoryPath)
        : Metadata.StreamTitle;

    public string DisplayDate
    {
        get
        {
            var value = Metadata.StreamStartedAtUtc ?? Metadata.LogStartedAtLocal;
            return value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }
    }

    public string MessageCountText => Metadata.MessageCount.ToString("N0", CultureInfo.CurrentCulture);

    public override string ToString() => $"{DisplayDate} · {DisplayTitle}";
}
