namespace WitherChat.Models;

public sealed class TwitchTokenSet
{
    public string ClientId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string TokenType { get; set; } = "bearer";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public List<string> Scopes { get; set; } = [];
    public string? UserId { get; set; }
    public string? Login { get; set; }
    public string? DisplayName { get; set; }
    public string? ProfileImageUrl { get; set; }
    public DateTimeOffset LastValidatedAtUtc { get; set; }

    public bool IsForClient(string clientId) =>
        string.Equals(ClientId, clientId, StringComparison.Ordinal);

    public bool IsAccessTokenNearExpiry(TimeSpan safetyWindow) =>
        DateTimeOffset.UtcNow.Add(safetyWindow) >= ExpiresAtUtc;
}
