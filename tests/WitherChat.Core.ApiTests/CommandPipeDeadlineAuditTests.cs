using System.Diagnostics;
using WitherChat.Core.Services;
using Xunit;
namespace WitherChat.Core.ApiTests;

public sealed class CommandPipeDeadlineAuditTests
{
    private static ProcessStartInfo Command(string windows, string unix)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh");
        if (OperatingSystem.IsWindows()) { info.ArgumentList.Add("/d"); info.ArgumentList.Add("/c"); }
        else info.ArgumentList.Add("-c");
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? windows : unix);
        return info;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelperThatDoesNotReadInputCannotDefeatDeadline(bool largeInput)
    {
        var watch = Stopwatch.StartNew();
        var result = BoundedCommandRunner.Run(
            Command("ping -n 8 127.0.0.1 >nul", "sleep 8"),
            largeInput ? new string('x', 2 * 1024 * 1024) : null, TimeSpan.FromMilliseconds(500));
        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "Owned helper exceeded its deadline.");
    }

    [Fact]
    public void FullStderrPipeDoesNotDeadlockReadingStdout()
    {
        var result = BoundedCommandRunner.Run(Command(
            "(for /L %i in (1,1,12000) do @echo synthetic-error 1>&2) & echo completed",
            "i=0; while [ $i -lt 12000 ]; do echo synthetic-error >&2; i=$((i+1)); done; echo completed"),
            null, TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("completed", result.Output, StringComparison.Ordinal);
    }
}
