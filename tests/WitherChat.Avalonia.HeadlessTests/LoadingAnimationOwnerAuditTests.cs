using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.Controls;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class LoadingAnimationOwnerAuditTests
{
    [AvaloniaFact]
    public void PreviousWindowFrameCannotModifyTheNewOwnersAnimation()
    {
        var bar = new CompactLoadingBar { Height = 16 };
        var first = new Window { Width = 200, Height = 100, Content = bar };
        var second = new Window { Width = 200, Height = 100 };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var generation = typeof(CompactLoadingBar).GetField("_frameGeneration", flags)!;
        var scheduled = typeof(CompactLoadingBar).GetField("_frameScheduled", flags)!;
        var time = typeof(CompactLoadingBar).GetField("_animationTime", flags)!;
        var callback = typeof(CompactLoadingBar).GetMethod("OnAnimationFrame", flags)!;
        try
        {
            first.Show();
            var oldGeneration = (int)generation.GetValue(bar)!;
            first.Content = null;
            second.Content = bar;
            second.Show();
            Assert.True((int)generation.GetValue(bar)! > oldGeneration);
            Assert.True((bool)scheduled.GetValue(bar)!);
            var expectedTime = TimeSpan.FromMilliseconds(1000);
            time.SetValue(bar, expectedTime);
            callback.Invoke(bar, [oldGeneration, TimeSpan.FromMilliseconds(2000)]);
            Assert.True((bool)scheduled.GetValue(bar)!);
            Assert.Equal(expectedTime, time.GetValue(bar));
        }
        finally { first.Close(); second.Close(); }
    }
}
