using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;

namespace TwitchChatMvp.Models;

public sealed class ChatMessageModel
{
    private static readonly Brush DefaultNameBrush = CreateDefaultNameBrush();

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string UserId { get; init; } = string.Empty;
    public string Login { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Color { get; init; } = string.Empty;
    public bool IsLocalEcho { get; init; }
    public ObservableCollection<BadgeModel> Badges { get; init; } = new();
    public ObservableCollection<ChatMessagePartModel> Parts { get; init; } = new();

    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
    public string LoginLabel => string.IsNullOrWhiteSpace(Login) ? "сообщение в чате" : "@" + Login;
    public string AvatarInitial => string.IsNullOrWhiteSpace(UserLabel)
        ? "?"
        : UserLabel.Trim()[0].ToString().ToUpperInvariant();
    public string TimeText => Timestamp.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public bool IsModerator => HasBadge("moderator") || HasBadge("broadcaster");
    public bool IsVip => HasBadge("vip");
    public bool IsSubscriber => HasBadge("subscriber") || HasBadge("founder");
    public bool HasBadges => Badges.Count > 0;

    public Brush NickBrush
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Color))
            {
                try
                {
                    return (Brush)new BrushConverter().ConvertFromString(Color)!;
                }
                catch
                {
                    return DefaultNameBrush;
                }
            }

            return DefaultNameBrush;
        }
    }

    private bool HasBadge(string setId)
    {
        return Badges.Any(b => string.Equals(b.SetId, setId, StringComparison.OrdinalIgnoreCase));
    }

    private static Brush CreateDefaultNameBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 255));
        brush.Freeze();
        return brush;
    }
}
