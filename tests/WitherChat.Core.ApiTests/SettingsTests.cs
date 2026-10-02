using WitherChat.Core.Models;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class SettingsTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("System")]
    public void EveryThemeOfferedByTheUiSurvivesNormalization(string theme)
    {
        var settings = new WitherChatSettings { Theme = theme };

        settings.Normalize();

        Assert.Equal(theme, settings.Theme);
    }

    [Fact]
    public void UnknownThemeFallsBackToDark()
    {
        var settings = new WitherChatSettings { Theme = "unknown" };

        settings.Normalize();

        Assert.Equal("Dark", settings.Theme);
    }

    [Fact]
    public void InvalidActiveChannelCannotBypassSavedChannelValidation()
    {
        var settings = new WitherChatSettings
        {
            Channel = "../../not-a-channel",
            SavedChannels = ["VALID_Channel", "also-valid", new string('a', 26)]
        };

        settings.Normalize();

        Assert.Equal(string.Empty, settings.Channel);
        Assert.Equal(["valid_channel"], settings.SavedChannels);
    }
}
