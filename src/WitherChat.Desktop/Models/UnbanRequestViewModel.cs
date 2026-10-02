using System.Globalization;
using WitherChat.Core.Models;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Models;

public sealed record UnbanRequestViewModel(UnbanRequest Value, UiText? Texts = null)
{
    private UiText LocalizedTexts { get; } = Texts ?? new UiText();
    public string UserLabel => string.IsNullOrWhiteSpace(Value.DisplayName) ? Value.UserLogin : Value.DisplayName;
    public string LoginLabel => "@" + Value.UserLogin;
    public string CreatedAtLabel => Value.CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string Text => Value.RequestText;
    public string StatusLabel => Value.Status switch
    {
        UnbanRequestStatus.Pending => LocalizedTexts.UnbanStatusPending,
        UnbanRequestStatus.Approved => LocalizedTexts.UnbanStatusApproved,
        UnbanRequestStatus.Denied => LocalizedTexts.UnbanStatusDenied,
        UnbanRequestStatus.Acknowledged => LocalizedTexts.UnbanStatusAcknowledged,
        UnbanRequestStatus.Canceled => LocalizedTexts.UnbanStatusCanceled,
        _ => Value.Status.ToString()
    };
    public string ResolutionText => Value.ResolutionText;
    public bool HasResolutionText => !string.IsNullOrWhiteSpace(Value.ResolutionText);
    public bool CanResolve => Value.Status == UnbanRequestStatus.Pending;
}
