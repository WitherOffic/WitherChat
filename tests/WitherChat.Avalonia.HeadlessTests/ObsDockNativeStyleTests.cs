using WitherChat.Desktop.Platforms;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;
public sealed class ObsDockNativeStyleTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(0x80CF0000L)]
    [InlineData(0xA1CF0000L)]
    [InlineData(0x10CF0000L)]
    public void EmbeddedStyleAlwaysShowsAChildAndRemovesDesktopState(long original)
    {
        var style = (long)WindowsObsDockHost.EmbeddedStyle((nint)original);
        Assert.NotEqual(0, style & 0x10000000L);
        Assert.NotEqual(0, style & 0x40000000L);
        Assert.Equal(0, style & 0xA1CF0000L);
    }
}
