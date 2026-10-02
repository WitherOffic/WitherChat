using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WitherChat.Core.Models;

namespace WitherChat.Desktop.Models;

public sealed partial class HeldAutoModMessageViewModel(HeldAutoModMessage value) : ObservableObject
{
    public HeldAutoModMessage Value { get; } = value;
    public string UserLabel => string.IsNullOrWhiteSpace(Value.UserDisplayName) ? Value.UserLogin : Value.UserDisplayName;
    public string LoginLabel => string.IsNullOrWhiteSpace(Value.UserLogin) ? string.Empty : "@" + Value.UserLogin;
    public string MessageText => Value.MessageText;
    public string ChannelLabel => string.IsNullOrWhiteSpace(Value.ChannelLogin) ? string.Empty : "@" + Value.ChannelLogin;
    public string TimeText => Value.HeldAt.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string CategoryLabel => string.IsNullOrWhiteSpace(Value.Category)
        ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{Value.Category} · {Value.Level}");

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorMessage = string.Empty;
}
