using WitherChat.ViewModels;
using WitherChat.Services;

namespace WitherChat.Models;

public sealed class BannedUserEntry : ObservableObject
{
    private bool _isRemoving;
    private string _errorMessage = string.Empty;

    public string UserId { get; init; } = string.Empty;
    public string UserLogin { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool IsPermanent => ExpiresAt is null;
    public bool IsRemoving { get => _isRemoving; set => SetProperty(ref _isRemoving, value); }
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? UserLogin : DisplayName;
    public string ActionLabel => LocalizationService.Get(
        LocalizationService.CurrentLanguage,
        IsPermanent ? "Unban" : "RemoveTimeout");
}
