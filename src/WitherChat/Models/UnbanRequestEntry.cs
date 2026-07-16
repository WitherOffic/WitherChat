using System.Globalization;
using WitherChat.ViewModels;

namespace WitherChat.Models;

public enum UnbanRequestStatus
{
    Pending,
    Approved,
    Denied,
    Acknowledged,
    Canceled
}

public sealed class UnbanRequestEntry : ObservableObject
{
    private UnbanRequestStatus _status;
    private DateTimeOffset? _resolvedAt;
    private string _resolutionText = string.Empty;
    private string _moderatorId = string.Empty;
    private string _moderatorName = string.Empty;
    private string _profileImageUrl = string.Empty;
    private bool _isActionInProgress;
    private string _errorMessage = string.Empty;

    public string RequestId { get; init; } = string.Empty;
    public string BroadcasterId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string UserLogin { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RequestText { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string ProfileImageUrl { get => _profileImageUrl; set => SetProperty(ref _profileImageUrl, value); }
    public UnbanRequestStatus Status { get => _status; set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(CanResolve)); } }
    public DateTimeOffset? ResolvedAt { get => _resolvedAt; set => SetProperty(ref _resolvedAt, value); }
    public string ResolutionText { get => _resolutionText; set => SetProperty(ref _resolutionText, value); }
    public string ModeratorId { get => _moderatorId; set => SetProperty(ref _moderatorId, value); }
    public string ModeratorName { get => _moderatorName; set => SetProperty(ref _moderatorName, value); }
    public bool IsActionInProgress { get => _isActionInProgress; set { if (SetProperty(ref _isActionInProgress, value)) OnPropertyChanged(nameof(CanResolve)); } }
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
    public string UserLabel => string.IsNullOrWhiteSpace(DisplayName) ? UserLogin : DisplayName;
    public string LoginLabel => string.IsNullOrWhiteSpace(UserLogin) ? string.Empty : "@" + UserLogin;
    public string CreatedAtText => CreatedAt.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
    public string AvatarInitial => string.IsNullOrWhiteSpace(UserLabel) ? "?" : UserLabel.Trim()[0].ToString().ToUpperInvariant();
    public bool CanResolve => Status == UnbanRequestStatus.Pending && !IsActionInProgress;
}
