using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class AppDiagnosticsTests
{
    [Fact]
    public void DiagnosticTextRedactsTokensAndAuthorizationHeaders()
    {
        const string source =
            "Authorization: Bearer abc.def-123 access_token=secret-token " +
            "{\"refresh_token\":\"another-secret\",\"widgetToken\":\"widget-secret\"}\r\n" +
            "Authorization: OAuth oauth-header-secret\r\n" +
            "Authorization: Basic basic-header-secret";

        var sanitized = AppDiagnostics.Sanitize(source);

        Assert.DoesNotContain("abc.def-123", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("widget-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-header-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("basic-header-secret", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessageIsSingleLineAndBounded()
    {
        var exception = new InvalidOperationException(
            "Failure\r\naccess_token=secret " + new string('x', 400));

        var message = AppDiagnostics.GetUserMessage(exception);

        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain('\n', message);
        Assert.True(message.Length <= 320);
    }
}
