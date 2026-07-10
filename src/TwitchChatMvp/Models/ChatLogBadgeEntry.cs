using System.Text.Json.Serialization;

namespace TwitchChatMvp.Models;

public sealed class ChatLogBadgeEntry
{
    public string SetId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Info { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Info))
            {
                return $"{SetId}:{Info}";
            }

            if (!string.IsNullOrWhiteSpace(Id))
            {
                return $"{SetId}:{Id}";
            }

            return SetId;
        }
    }
}
