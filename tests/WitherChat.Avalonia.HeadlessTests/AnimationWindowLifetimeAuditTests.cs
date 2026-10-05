using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class AnimationWindowLifetimeAuditTests
{
    [AvaloniaTheory]
    [InlineData(280, 340, false)]
    [InlineData(280, 340, true)]
    [InlineData(1100, 760, false)]
    [InlineData(1100, 760, true)]
    public void ClosingWindowBeforeItsNextAnimationFrameReleasesTheScheduledWindow(
        int width, int height, bool pauseBeforeClosing)
    {
        using var media = ChatImageDecoder.Decode(Convert.FromBase64String(
            "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
            "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7"));
        var resource = new ChatImageResource(28);
        resource.SetMedia(media!, 28);
        var window = new Window
        {
            Width = width, Height = height,
            Content = new AnimatedEmoteImage { Resource = resource }
        };
        var type = typeof(AnimatedEmoteImage);
        var oldReduceMotion = (bool)type.GetField("_reduceMotion",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var oldFastScrolling = (bool)type.GetField("_fastScrolling",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var scheduled = (HashSet<TopLevel>)type.GetField("ScheduledTopLevels",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            AnimatedEmoteImage.SetReduceMotion(false);
            AnimatedEmoteImage.SetFastScrolling(false);
            window.Show();
            Assert.Contains(window, scheduled);
            AnimatedEmoteImage.SetReduceMotion(pauseBeforeClosing);
            window.Close();
            Assert.DoesNotContain(window, scheduled);
        }
        finally
        {
            window.Close();
            scheduled.Remove(window); // Keep the intentionally failing baseline test isolated.
            AnimatedEmoteImage.SetReduceMotion(oldReduceMotion);
            AnimatedEmoteImage.SetFastScrolling(oldFastScrolling);
        }
    }
}
