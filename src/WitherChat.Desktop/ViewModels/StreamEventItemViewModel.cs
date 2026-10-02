using WitherChat.Core.Models;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.ViewModels;

public sealed class StreamEventItemViewModel(StreamEvent value, UiText texts)
{
    public StreamEvent Value { get; } = value;
    public string Platform => Value.Platform;
    public string PlatformMark => Value.Platform == ChatPlatforms.YouTube ? "YT" :
        Value.Platform == "DonationAlerts" ? "DA" : "TW";
    public string UserLabel => string.IsNullOrWhiteSpace(Value.UserLabel) ? texts.DonationAnonymous : Value.UserLabel;
    public string Title => texts.StreamEventTitle(Value.Kind, Value.Count);
    public string Message => Value.Message;
    public string AmountLabel => Value.AmountDisplay;
    public string DetailLabel => Value.Detail;
    public string TimeLabel => Value.Timestamp.LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasAmount => !string.IsNullOrWhiteSpace(AmountLabel);
    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailLabel);
    public bool IsPaid => Value.IsPaid;
}
