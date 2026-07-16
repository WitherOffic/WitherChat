namespace WitherChat.Models;

public sealed record TwitchBadgeDefinition(
    string SetId,
    string Id,
    string ImageUrl1x,
    string ImageUrl2x,
    string ImageUrl4x,
    string Title);
