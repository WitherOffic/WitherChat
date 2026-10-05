using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class HiddenAnimationAuditTests
{
    private const string Gif = "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
        "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7";

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvisibleEmoteStopsAndVisibleEmoteResumes(bool hideParent)
    {
        using var media = ChatImageDecoder.Decode(Convert.FromBase64String(Gif));
        var resource = new ChatImageResource(28);
        resource.SetMedia(media!, 28);
        var image = new AnimatedEmoteImage { Resource = resource };
        var parent = new Border { Child = image };
        var active = (HashSet<AnimatedEmoteImage>)typeof(AnimatedEmoteImage).GetField(
            "ActiveImages", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var hidden = hideParent ? (Control)parent : image;
        hidden.IsVisible = false;
        var window = new Window { Width = 200, Height = 100, Content = parent };
        try
        {
            window.Show();
            using var first = window.CaptureRenderedFrame();
            Assert.DoesNotContain(image, active);
            hidden.IsVisible = true;
            Assert.Contains(image, active);
            hidden.IsVisible = false;
            using var last = window.CaptureRenderedFrame();
            Assert.DoesNotContain(image, active);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LoadingIndicatorStopsWithHiddenParentAndResumesWhenShown()
    {
        var bar = new CompactLoadingBar { Height = 16 };
        var parent = new Border { Child = bar, IsVisible = false };
        var scheduled = typeof(CompactLoadingBar).GetField(
            "_frameScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var window = new Window { Width = 200, Height = 100, Content = parent };
        try
        {
            window.Show();
            using var hidden = window.CaptureRenderedFrame();
            Assert.False((bool)scheduled.GetValue(bar)!);
            parent.IsVisible = true;
            using var shown = window.CaptureRenderedFrame();
            Assert.True((bool)scheduled.GetValue(bar)!);
            parent.IsVisible = false;
            using var stopped = window.CaptureRenderedFrame();
            Assert.False((bool)scheduled.GetValue(bar)!);
        }
        finally { window.Close(); }
    }
}
