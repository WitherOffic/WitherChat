using Avalonia;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class CompactScalingAuditTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(1.25, 1.5)]
    [InlineData(1.5, 1)]
    [InlineData(2, 1.25)]
    [InlineData(1, 1)]
    [InlineData(1.5, 3)]
    public void ExpandedTargetRecalculatesPixelsForTheCurrentMonitor(double previousScale, double currentScale)
    {
        var previous = MainWindow.GetExpandedTargetMetrics(new Size(1100, 760), previousScale);
        var restored = MainWindow.GetExpandedTargetMetrics(previous.LogicalSize, currentScale);
        Assert.Equal(new Size(1100, 760), restored.LogicalSize);
        Assert.Equal(new PixelSize((int)Math.Round(1100 * currentScale), (int)Math.Round(760 * currentScale)),
            restored.PixelSize);
        if (previousScale != currentScale)
        {
            Assert.NotEqual(previous.PixelSize, restored.PixelSize);
        }
    }

    [Theory]
    [InlineData(0.75)]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExpandedTargetPreservesMinimumLogicalSizeAtEveryScale(double currentScale)
    {
        var target = MainWindow.GetExpandedTargetMetrics(new Size(360, 400), currentScale);
        Assert.Equal(new Size(860, 560), target.LogicalSize);
        Assert.Equal(new PixelSize((int)Math.Round(860 * currentScale), (int)Math.Round(560 * currentScale)),
            target.PixelSize);
    }
}
