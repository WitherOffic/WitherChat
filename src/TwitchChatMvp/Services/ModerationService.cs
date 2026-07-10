namespace TwitchChatMvp.Services;

public sealed class ModerationService
{
    private readonly TwitchApiClient _apiClient;

    public ModerationService(TwitchApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public Task BanUserAsync(
        string broadcasterId,
        string moderatorId,
        string targetUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return _apiClient.BanOrTimeoutUserAsync(broadcasterId, moderatorId, targetUserId, reason, null, cancellationToken);
    }

    public Task TimeoutUserAsync(
        string broadcasterId,
        string moderatorId,
        string targetUserId,
        int durationSeconds,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive.");
        }

        return _apiClient.BanOrTimeoutUserAsync(broadcasterId, moderatorId, targetUserId, reason, durationSeconds, cancellationToken);
    }
}
