using System.Text.Json;
using WitherChat.Core.Models;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class ObsPluginSettingsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("  D:\\OBS Studio  ", "D:\\OBS Studio")]
    [InlineData("", "")]
    public void ObsFolderIsNullSafeAndSurvivesSerialization(string? directory, string expected)
    {
        var settings = new WitherChatSettings { ObsPluginDirectory = directory! };
        settings.Normalize();
        var restored = JsonSerializer.Deserialize<WitherChatSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(expected, restored.ObsPluginDirectory);
    }
}
