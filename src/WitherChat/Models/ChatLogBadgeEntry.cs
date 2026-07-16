using System.Text.Json.Serialization;

namespace WitherChat.Models;

public sealed class ChatLogBadgeEntry
{
    public string SetId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Info { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);

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
