using CommunityToolkit.Mvvm.ComponentModel;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Models;

public sealed partial class ChannelSessionViewModel(string login) : ObservableObject
{
    public string Login { get; } = login;
    public string Label => "@" + Login;
    public bool CanRemove => !IsPrimaryAccountChannel;
    public bool HasProfileImage => ProfileImageResource is not null;
    public bool HasViewerCount => IsLive;
    public string AvatarInitial => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : DisplayName.Trim()[0].ToString().ToUpperInvariant();

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool _isPrimaryAccountChannel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarInitial))]
    private string _displayName = login;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileImage))]
    private ChatImageResource? _profileImageResource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewerCount))]
    private bool _isLive;

    [ObservableProperty]
    private int _viewerCount;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _broadcasterId = string.Empty;
}
