using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class StreamMomentStore : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly AppDataPaths _paths;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _savesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingSaves;
    private bool _savesBlockedAfterLoadFailure;
    private bool _disposed;
    public bool HadLoadFailure { get; private set; }
    public string DirectoryPath => _paths.ConfigDirectory;

    public StreamMomentStore(AppDataPaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public IReadOnlyList<StreamMoment> Load()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        try
        {
            var moments = ReadMoments(_paths.MomentsFile);
            _savesBlockedAfterLoadFailure = false;
            HadLoadFailure = false;
            return moments;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            try
            {
                var recovered = ReadMoments(_paths.MomentsFile + ".bak");
                _savesBlockedAfterLoadFailure = false;
                HadLoadFailure = true;
                return recovered;
            }
            catch (Exception backupException) when (backupException is FileNotFoundException or DirectoryNotFoundException)
            {
                _savesBlockedAfterLoadFailure = false;
                HadLoadFailure = false;
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException or JsonException)
            {
                _savesBlockedAfterLoadFailure = backupException is not (JsonException or InvalidDataException);
                HadLoadFailure = true;
            }
            return [];
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            HadLoadFailure = true;
            IReadOnlyList<StreamMoment> recovered = [];
            var backupReadFailed = false;
            try { recovered = ReadMoments(_paths.MomentsFile + ".bak"); }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException or JsonException)
            {
                // Keep the original backup for recovery/diagnostics.
                backupReadFailed = backupException is not (FileNotFoundException or DirectoryNotFoundException or
                    JsonException or InvalidDataException);
            }
            try
            {
                var quarantine = _paths.MomentsFile + ".corrupt-" +
                    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                File.Move(_paths.MomentsFile, quarantine);
                _savesBlockedAfterLoadFailure = backupReadFailed;
            }
            catch (Exception preserveException) when (preserveException is IOException or UnauthorizedAccessException)
            {
                _savesBlockedAfterLoadFailure = true;
            }
            return recovered;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A transient read failure must not allow an empty UI snapshot to
            // overwrite existing moments after access returns.
            HadLoadFailure = true;
            _savesBlockedAfterLoadFailure = true;
            return [];
        }
    }

    private static IReadOnlyList<StreamMoment> ReadMoments(string path)
    {
        var values = JsonSerializer.Deserialize<StreamMoment?[]>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("The moments file contains no list.");
        return values.Where(IsValid).Select(value => value!).TakeLast(5000).ToArray();
    }

    public async Task SaveAsync(IReadOnlyCollection<StreamMoment> moments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moments);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingSaves++;
        }
        var gateHeld = false;
        try
        {
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            if (_savesBlockedAfterLoadFailure)
                throw new IOException("Existing moments could not be read. Reload them before saving.");
            _paths.EnsureCreated();
            var target = _paths.MomentsFile;
            var temporary = target + ".tmp";
            try
            {
                var snapshot = moments.Where(IsValid).TakeLast(5000).ToArray();
                await File.WriteAllTextAsync(
                    temporary, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(target))
                    File.Replace(temporary, target, target + ".bak");
                else
                    File.Move(temporary, target);
                HadLoadFailure = false;
            }
            finally
            {
                TryDeleteTemporaryFile(temporary);
            }
        }
        finally
        {
            if (gateHeld) _saveGate.Release();
            lock (_lifetimeGate)
            {
                if (--_pendingSaves == 0 && _disposed)
                {
                    _saveGate.Dispose();
                    _savesDrained.TrySetResult();
                }
            }
        }
    }

    private static bool IsValid(StreamMoment? value) =>
        value is not null && !string.IsNullOrWhiteSpace(value.Id) && !string.IsNullOrWhiteSpace(value.Channel);

    private static void TryDeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            // Accepted saves own the gate until they finish. Never block the UI
            // or dispose the semaphore while a writer can still release it.
            if (_pendingSaves == 0)
            {
                _saveGate.Dispose();
                _savesDrained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(_savesDrained.Task);
    }
}
