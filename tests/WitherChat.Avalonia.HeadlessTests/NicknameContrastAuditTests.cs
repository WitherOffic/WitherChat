using Avalonia.Media;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class NicknameContrastAuditTests
{
    [AvaloniaTheory]
    [InlineData("", true)]
    [InlineData("invalid", true)]
    [InlineData("#FFFFFF", true)]
    [InlineData("#FFFF00", true)]
    [InlineData("#00FF00", true)]
    [InlineData("#9BB6FF", true)]
    [InlineData("#000000", false)]
    [InlineData("#0000FF", false)]
    [InlineData("Transparent", true)]
    public void NicknamesRemainReadableOnNormalAndHighlightedSurfaces(string color, bool light)
    {
        using var cache = new ChatImageCache();
        var item = new ChatMessageItemViewModel(new ChatMessage
        {
            Id = "contrast", Channel = "audit", UserLogin = "viewer",
            DisplayName = "Viewer", Text = "message", UserColor = color, Timestamp = DateTimeOffset.UtcNow
        }, cache, new UiText());
        item.ApplyPresentationSettings(true, true, true, null, light);
        var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(item.UserBrush).Color;
        var backgrounds = light ? new[] { "#FFFFFF", "#D7DDF0" } : new[] { "#0F111B", "#252833" };
        Assert.Equal(255, foreground.A);
        foreach (var background in backgrounds)
            Assert.True(Contrast(foreground, Color.Parse(background)) >= 4.5,
                $"Unreadable nickname {color}: rendered={foreground}, background={background}");
    }

    [AvaloniaFact]
    public void ThemeChangesNotifyExistingNicknameBindingsWithoutChangingMessageColor()
    {
        using var cache = new ChatImageCache();
        var item = new ChatMessageItemViewModel(new ChatMessage
        {
            Id = "contrast", Channel = "audit", UserLogin = "viewer", DisplayName = "Viewer",
            UserColor = "#FFFF00", Text = "message", Timestamp = DateTimeOffset.UtcNow
        }, cache, new UiText());
        var initial = Assert.IsAssignableFrom<ISolidColorBrush>(item.UserBrush).Color;
        var notifications = new List<string?>();
        item.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        item.ApplyPresentationSettings(true, true, true, null, true);
        Assert.Contains(nameof(item.UserBrush), notifications);
        Assert.NotEqual(initial, Assert.IsAssignableFrom<ISolidColorBrush>(item.UserBrush).Color);
        Assert.Equal("#FFFF00", item.Message.UserColor);
        item.ApplyPresentationSettings(true, true, true, null, false);
        Assert.Equal(initial, Assert.IsAssignableFrom<ISolidColorBrush>(item.UserBrush).Color);
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color value) =>
            0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B);
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
