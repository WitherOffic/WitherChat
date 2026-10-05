using System.IO.Pipes;
using System.Text;
using WitherChat.Core.Services;

namespace WitherChat.Desktop.Services;

// No tokens or chat data travel through this pipe. Access is limited to the current Windows user.
internal sealed class ObsDockIpcService : IAsyncDisposable
{
    internal const string PipeName = "WitherChat-ObsDock-v1-9E69A68D-87D9-47F1-99AE-F35AA2DCC3EA";
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(1_500);
    private readonly Func<ObsDockRequest, CancellationToken, Task<string>> _handle;
    private readonly TimeSpan _requestTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _listener;
    private readonly string _pipeName;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    internal ObsDockIpcService(Func<ObsDockRequest, Task<string>> handle, string? pipeName = null)
        : this(AdaptHandler(handle), pipeName) { }

    internal ObsDockIpcService(
        Func<ObsDockRequest, CancellationToken, Task<string>> handle,
        string? pipeName = null, TimeSpan? requestTimeout = null)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _requestTimeout = requestTimeout ?? RequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _pipeName = pipeName ?? SessionPipeName.ForCurrentSession(PipeName);
        _listener = ListenAsync(_shutdown.Token);
    }

    private static Func<ObsDockRequest, CancellationToken, Task<string>> AdaptHandler(
        Func<ObsDockRequest, Task<string>> handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return (request, _) => handle(request);
    }

    internal static async Task<bool> RequestAsync(
        ObsDockRequest request, string? pipeName = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName ?? SessionPipeName.ForCurrentSession(PipeName),
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var bytes = Encoding.ASCII.GetBytes(request + "\n");
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            var response = await ReadLineAsync(client, timeout.Token).ConfigureAwait(false);
            return response is not null && response.StartsWith("OK ", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[129];
        for (var length = 0; length < bytes.Length; length++)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (count == 0) return null;
            if (bytes[length] == '\n') return Encoding.ASCII.GetString(bytes, 0, length).TrimEnd('\r');
            if (bytes[length] is < 32 or > 126 && bytes[length] != '\r') return null;
        }
        return null;
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                // The native plugin waits two seconds for a reply. Cancel stale
                // UI work sooner, leaving time to return an explicit error frame.
                timeout.CancelAfter(_requestTimeout);
                string response;
                try
                {
                    var command = await ReadLineAsync(server, timeout.Token).ConfigureAwait(false);
                    response = ObsDockRequest.TryParse(command, out var request)
                        ? await _handle(request, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false)
                        : "ERROR invalid-command";
                }
                catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
                {
                    response = "ERROR request-timeout";
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                writeTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
                await server.WriteAsync(Encoding.ASCII.GetBytes(response + "\n"), writeTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A disconnected or stalled client must not stop the next attach request.
                // A competing server can also make construction fail synchronously.
                // Yield between retries so it cannot block startup or spin at 100% CPU.
                await Task.Delay(150, stopping).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppDiagnostics.Write("OBS dock IPC", exception);
                await Task.Delay(150, stopping).ConfigureAwait(false);
            }
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

        // Publish completion before cancellation callbacks can reenter disposal.
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Cleanup failures propagate through the shared completion task to all callers.")]
    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
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

    private async Task DisposeCoreAsync()
    {
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        finally
        {
            try { await _listener.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { _shutdown.Dispose(); }
        }
    }
}
