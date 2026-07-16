using System.Globalization;
using System.IO;
using WitherChat.Services;

namespace WitherChat.Models;

public sealed class ChatLogSessionSummary
{
    public string DirectoryPath { get; init; } = string.Empty;
    public ChatLogSessionMetadata Metadata { get; init; } = new();

    public string DisplayTitle
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Metadata.StreamTitle)
                ? Path.GetFileName(DirectoryPath)
                : Metadata.StreamTitle;
            if (string.Equals(Metadata.LogMode, "daily", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(Metadata.StreamTitle) ||
                 Metadata.StreamTitle.StartsWith("offline_chat", StringComparison.OrdinalIgnoreCase)))
            {
                return LocalizationService.Get(LocalizationService.CurrentLanguage, "DailyChatLog");
            }

            return title.StartsWith("offline_chat", StringComparison.OrdinalIgnoreCase)
                ? LocalizationService.Get(LocalizationService.CurrentLanguage, "OfflineChat")
                : title;
        }
    }

    public string DisplayDate
    {
        get
        {
            var value = string.Equals(Metadata.LogMode, "daily", StringComparison.OrdinalIgnoreCase)
                ? Metadata.LogStartedAtLocal
                : Metadata.StreamStartedAtUtc ?? Metadata.LogStartedAtLocal;
            var format = string.Equals(Metadata.LogMode, "daily", StringComparison.OrdinalIgnoreCase)
                ? "yyyy-MM-dd"
                : "yyyy-MM-dd HH:mm";
            return value.LocalDateTime.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    public string MessageCountText => Metadata.MessageCount.ToString("N0", CultureInfo.CurrentCulture);

    public override string ToString() => $"{DisplayDate} · {DisplayTitle}";
}
