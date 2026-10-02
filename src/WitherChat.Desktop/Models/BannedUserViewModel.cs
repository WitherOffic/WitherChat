using System.Globalization;
using WitherChat.Core.Models;

namespace WitherChat.Desktop.Models;

public sealed record BannedUserViewModel(BannedUser Value)
{
    public string UserLabel => string.IsNullOrWhiteSpace(Value.DisplayName) ? Value.UserLogin : Value.DisplayName;
    public string LoginLabel => "@" + Value.UserLogin;
    public string DurationLabel => Value.ExpiresAt is null
        ? "∞"
        : Value.ExpiresAt.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Reason => Value.Reason;
}
