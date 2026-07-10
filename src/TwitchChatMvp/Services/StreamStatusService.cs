using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class StreamStatusService
{
    private readonly TwitchApiClient _apiClient;
    private readonly FileLogger _logger;

    public StreamStatusService(TwitchApiClient apiClient, FileLogger logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task<StreamStatusInfo> GetStatusAsync(string broadcasterId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _apiClient.GetStreamStatusAsync(broadcasterId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Stream status check failed: {ex.GetType().Name}");
            return new StreamStatusInfo(false, 0, string.Empty);
        }
    }
}
