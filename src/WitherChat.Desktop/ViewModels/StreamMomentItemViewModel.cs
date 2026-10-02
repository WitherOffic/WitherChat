using WitherChat.Core.Models;

namespace WitherChat.Desktop.ViewModels;

public sealed class StreamMomentItemViewModel
{
    public StreamMomentItemViewModel(StreamMoment value) => Value = value;

    public StreamMoment Value { get; }
    public string Platform => Value.Platform;
    public string ChannelLabel => "@" + Value.Channel;
    public string UserLabel => Value.User;
    public string Text => Value.Text;
    public string Note => Value.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Value.Note);
    public string TimeLabel => Value.MessageTimestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
}
