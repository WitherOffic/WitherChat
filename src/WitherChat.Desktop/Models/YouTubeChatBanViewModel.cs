using System.Globalization;
using WitherChat.Core.Models;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Models;

public sealed class YouTubeChatBanViewModel(YouTubeChatBan value, UiText texts)
{
    public YouTubeChatBan Value { get; } = value;
    public string UserLabel => string.IsNullOrWhiteSpace(Value.DisplayName)
        ? Value.UserChannelId
        : Value.DisplayName;
    public string ChannelIdLabel => Value.UserChannelId;
    public string DurationLabel => Value.EndsAt is null
        ? texts.PermanentBan
        : Value.EndsAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}
