using System.ComponentModel;
using System.Diagnostics;
namespace WitherChat.Core.Services;

internal readonly record struct BoundedCommandResult(int ExitCode, string Output);

internal static class BoundedCommandRunner
{
    internal static BoundedCommandResult Run(ProcessStartInfo startInfo, string? standardInput, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = standardInput is not null;
        using var process = Process.Start(startInfo);
        if (process is null) return new(-1, string.Empty);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            var input = standardInput is null ? Task.CompletedTask : WriteInputAsync(process, standardInput);
            var work = Task.WhenAll(output, error, input, process.WaitForExitAsync());
            try
            {
                work.WaitAsync(timeout).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                // The timed-out work can fault when its owned process/pipes are closed.
                _ = work.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return new(-1, string.Empty);
            }
            return new(process.ExitCode, output.GetAwaiter().GetResult());
        }
        finally
        {
            StopOwnedProcess(process);
        }
    }

    private static async Task WriteInputAsync(Process process, string input)
    {
        await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(1000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // A naturally exiting helper can race the timeout cleanup.
        }
    }
}
