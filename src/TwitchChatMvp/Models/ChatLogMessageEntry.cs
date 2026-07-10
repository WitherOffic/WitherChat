using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TwitchChatMvp.Models;

public sealed class ChatLogMessageEntry
{
    private static readonly Brush DefaultNameBrush = CreateDefaultNameBrush();

    public DateTimeOffset TimestampLocal { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string ChannelLogin { get; set; } = string.Empty;
    public string StreamTitle { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserLogin { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string UserColor { get; set; } = string.Empty;
    public List<ChatLogBadgeEntry> Badges { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public bool IsModerator { get; set; }
    public bool IsSubscriber { get; set; }
    public bool IsBroadcaster { get; set; }
    public bool IsVip { get; set; }

    [JsonIgnore]
    public string TimeText => TimestampLocal.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? UserLogin : DisplayName;

    [JsonIgnore]
    public string BadgeText => Badges.Count == 0 ? string.Empty : string.Join(" ", Badges.Select(b => b.DisplayText));

    [JsonIgnore]
    public Brush NickBrush
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(UserColor))
            {
                try
                {
                    return (Brush)new BrushConverter().ConvertFromString(UserColor)!;
                }
                catch
                {
                    return DefaultNameBrush;
                }
            }

            return DefaultNameBrush;
        }
    }

    public bool MatchesRole(string role)
    {
        return role switch
        {
            "broadcaster" => IsBroadcaster,
            "moderator" => IsModerator,
            "subscriber" => IsSubscriber,
            "vip" => IsVip,
            _ => true
        };
    }

    private static Brush CreateDefaultNameBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(180, 180, 255));
        brush.Freeze();
        return brush;
    }
}
