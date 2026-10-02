using System.Net;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class SessionResilienceTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void OnlyTerminalOAuthFailuresRemoveTheStoredSession(
        HttpStatusCode statusCode,
        bool expected)
    {
        var exception = new HttpRequestException("validation failed", null, statusCode);

        Assert.Equal(expected, MainWindowViewModel.IsTerminalSessionFailure(exception));
    }

    [Fact]
    public void MissingRequiredPermissionsAreTerminalButNetworkFailuresAreNot()
    {
        Assert.True(MainWindowViewModel.IsTerminalSessionFailure(
            new InvalidOperationException("missing required permissions")));
        Assert.False(MainWindowViewModel.IsTerminalSessionFailure(
            new HttpRequestException("temporary network failure")));
        Assert.False(MainWindowViewModel.IsTerminalSessionFailure(
            new TaskCanceledException("request timed out")));
    }
}
