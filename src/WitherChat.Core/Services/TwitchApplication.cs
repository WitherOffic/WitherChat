namespace WitherChat.Core.Services;

public static class TwitchApplication
{
    public const string ClientId = "f1v3cswx6e7w42gibf68m0ca8903g0";
    public const string RedirectUri = "http://localhost:17654/";
    public const string FollowedChannelsScope = "user:read:follows";
    public const string ClipsScope = "clips:edit";

    public static readonly IReadOnlyList<string> RequiredScopes =
    [
        "user:read:chat",
        "user:write:chat",
        "chat:read",
        "moderator:manage:banned_users",
        "user:read:moderated_channels",
        "channel:read:redemptions",
        "moderator:manage:chat_messages",
        "moderator:manage:chat_settings",
        "moderator:manage:automod",
        "channel:moderate",
        "moderator:manage:unban_requests"
    ];

    public static readonly IReadOnlyList<string> ChatScopes =
    [
        .. RequiredScopes,
        FollowedChannelsScope,
        ClipsScope
    ];
}
