using System.IO.Pipes;
using System.Text;

namespace WitherChat.Desktop.Services;

internal sealed class SingleInstanceActivationService : IAsyncDisposable
{
    internal const string DefaultPipeName = "WitherChat-Activate-9E69A68D-87D9-47F1-99AE-F35AA2DCC3EA";
    private const string ShowCommand = "SHOW";
    private readonly Action _showWindow;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public SingleInstanceActivationService(Action showWindow, string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        _showWindow = showWindow;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? SessionPipeName.ForCurrentSession(DefaultPipeName) : pipeName;
        _listenerTask = ListenAsync(_cancellation.Token);
    }

    public static async Task<bool> RequestShowWindowAsync(
        string? pipeName = null,
        CancellationToken cancellationToken = default)
    {
        pipeName = string.IsNullOrWhiteSpace(pipeName) ? SessionPipeName.ForCurrentSession(DefaultPipeName) : pipeName;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                GetPipeOptions());
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(ShowCommand.AsMemory(), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "All callers receive cleanup errors through the shared completion task.")]
    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            try { await _cancellation.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { await _listenerTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                finally { _cancellation.Dispose(); }
            }
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    GetPipeOptions());
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                // One incomplete client must not occupy the activation channel indefinitely.
                // SHOW is an ASCII command; share the bounded framing used by the OBS channel.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command = await ObsDockIpcService.ReadLineAsync(server, timeout.Token).ConfigureAwait(false);
                if (string.Equals(command, ShowCommand, StringComparison.Ordinal))
                {
                    _showWindow();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // A failed activation callback must not permanently kill the listener.
                AppDiagnostics.Write("Single-instance activation", exception);
                await DelayAfterFailureAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static PipeOptions GetPipeOptions() =>
        PipeOptions.Asynchronous |
        (OperatingSystem.IsWindows() ? PipeOptions.CurrentUserOnly : PipeOptions.None);

    private static async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
