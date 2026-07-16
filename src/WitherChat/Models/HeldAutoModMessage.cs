using System.Globalization;
using WitherChat.ViewModels;

namespace WitherChat.Models;

public enum HeldAutoModStatus
{
    Pending,
    Approved,
    Denied,
    Expired
}

public sealed class HeldAutoModMessage : ObservableObject
{
    private HeldAutoModStatus _status;
    private bool _isActionInProgress;
    private string _errorMessage = string.Empty;

    public string MessageId { get; init; } = string.Empty;
    public string BroadcasterId { get; init; } = string.Empty;
    public string ChannelLogin { get; set; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string UserLogin { get; init; } = string.Empty;
    public string UserDisplayName { get; init; } = string.Empty;
    public string MessageText { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public int Level { get; init; }
    public DateTimeOffset HeldAt { get; init; } = DateTimeOffset.UtcNow;
    public HeldAutoModStatus Status { get => _status; set => SetProperty(ref _status, value); }
    public bool IsActionInProgress { get => _isActionInProgress; set => SetProperty(ref _isActionInProgress, value); }
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
    public string UserLabel => string.IsNullOrWhiteSpace(UserDisplayName) ? UserLogin : UserDisplayName;
    public string LoginLabel => string.IsNullOrWhiteSpace(UserLogin) ? string.Empty : "@" + UserLogin;
    public string TimeText => HeldAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string CategoryLabel => string.IsNullOrWhiteSpace(Category) ? string.Empty : $"{Category} · {Level}";
}
