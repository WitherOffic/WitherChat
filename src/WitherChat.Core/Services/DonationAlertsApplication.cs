namespace WitherChat.Core.Services;

public static class DonationAlertsApplication
{
    public const string ClientId = "20400";
    public const string RedirectUri = "http://localhost:17656/";
    public const string RegistrationUrl = "https://www.donationalerts.com/application/clients";
    public const string UserScope = "oauth-user-show";
    public const string DonationSubscribeScope = "oauth-donation-subscribe";
    public const string DonationIndexScope = "oauth-donation-index";

    public static IReadOnlyList<string> RequiredScopes { get; } =
        [UserScope, DonationSubscribeScope, DonationIndexScope];

    public static bool HasRequiredScopes(IEnumerable<string>? scopes)
    {
        var grantedScopes = new HashSet<string>(scopes ?? [], StringComparer.Ordinal);
        return RequiredScopes.All(grantedScopes.Contains);
    }
}
