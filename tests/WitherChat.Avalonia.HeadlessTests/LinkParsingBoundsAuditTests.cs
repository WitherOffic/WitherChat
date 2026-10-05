using System.Reflection;
using System.Text.RegularExpressions;
using WitherChat.Desktop.Services;
using Xunit;
namespace WitherChat.Avalonia.HeadlessTests;
public sealed class LinkParsingBoundsAuditTests
{
    [Fact]
    public void UrlMatchingHasAFiniteWorkDeadline()
    {
        var regex = (Regex)typeof(ChatLinkParser).GetMethod("UrlRegex",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        Assert.NotEqual(Regex.InfiniteMatchTimeout, regex.MatchTimeout);
        Assert.InRange(regex.MatchTimeout.TotalMilliseconds, 1, 250);
    }

    [Theory]
    [InlineData(3000)]
    [InlineData(30000)]
    public void AdversarialDomainKeepsItsEntireTextWhenMatchingStops(int segments)
    {
        var text = string.Concat(Enumerable.Repeat("a.", segments)) + "0";
        var result = Assert.Single(ChatLinkParser.Parse(text));
        Assert.Equal(text, result.Text);
        Assert.False(result.IsLink);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    [InlineData(65536)]
    public void LongClosingPunctuationIsTrimmedWithoutLosingTheUrl(int count)
    {
        const string url = "https://example.com/path";
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var trimmed = (string)typeof(ChatLinkParser).GetMethod("TrimTrailingPunctuation",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [url + new string(')', count)])!;
        timer.Stop();
        Assert.Equal(url, trimmed);
        TestContext.Current.TestOutputHelper!.WriteLine($"closing={count}, milliseconds={timer.Elapsed.TotalMilliseconds:F3}");
    }

    [Theory]
    [InlineData("https://example.com/a(b)", "https://example.com/a(b)")]
    [InlineData("https://example.com/a(b)))", "https://example.com/a(b)")]
    [InlineData("https://example.com/path]]", "https://example.com/path")]
    [InlineData("https://example.com/[x]", "https://example.com/[x]")]
    public void BalancedUrlPunctuationRemainsIntact(string value, string expected)
    {
        var trimmed = (string)typeof(ChatLinkParser).GetMethod("TrimTrailingPunctuation",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [value])!;
        Assert.Equal(expected, trimmed);
    }
}
