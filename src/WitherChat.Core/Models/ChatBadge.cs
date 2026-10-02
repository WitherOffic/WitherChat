namespace WitherChat.Core.Models;

public sealed record ChatBadge(
    string SetId,
    string VersionId,
    string Info = "",
    Uri? ImageUri = null,
    string Title = "")
{
    public string Key => $"{SetId}/{VersionId}";
}
