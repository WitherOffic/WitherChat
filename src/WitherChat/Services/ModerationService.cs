using WitherChat.Models;

namespace WitherChat.Services;

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

    public Task DeleteMessageAsync(string broadcasterId, string moderatorId, string messageId, CancellationToken cancellationToken = default) =>
        _apiClient.DeleteChatMessageAsync(broadcasterId, moderatorId, messageId, cancellationToken);

    public Task UnbanOrUntimeoutAsync(string broadcasterId, string moderatorId, string targetUserId, CancellationToken cancellationToken = default) =>
        _apiClient.UnbanUserAsync(broadcasterId, moderatorId, targetUserId, cancellationToken);

    public Task ManageHeldAutoModMessageAsync(string moderatorUserId, string messageId, bool allow, CancellationToken cancellationToken = default) =>
        _apiClient.ManageHeldAutoModMessageAsync(moderatorUserId, messageId, allow, cancellationToken);

    public Task<BannedUsersPage> GetBannedUsersAsync(string broadcasterId, string? cursor = null, CancellationToken cancellationToken = default) =>
        _apiClient.GetBannedUsersAsync(broadcasterId, cursor, cancellationToken);

    public Task<UnbanRequestsPage> GetUnbanRequestsAsync(
        string broadcasterId,
        string moderatorId,
        UnbanRequestStatus status,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        _apiClient.GetUnbanRequestsAsync(broadcasterId, moderatorId, status, 100, cursor, cancellationToken);

    public Task<UnbanRequestEntry> ResolveUnbanRequestAsync(
        string broadcasterId,
        string moderatorId,
        string requestId,
        UnbanRequestStatus status,
        string resolutionText,
        CancellationToken cancellationToken = default) =>
        _apiClient.ResolveUnbanRequestAsync(broadcasterId, moderatorId, requestId, status, resolutionText, cancellationToken);
}
