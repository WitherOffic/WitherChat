using System.Globalization;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Models;

public sealed record RecentUserMessageViewModel(ChatMessageItemViewModel Value)
{
    public string TimeText => Value.Message.Timestamp.LocalDateTime.ToString(
        "dd.MM.yyyy HH:mm:ss",
        CultureInfo.CurrentCulture);

    public string Text => Value.Message.Text;
}
