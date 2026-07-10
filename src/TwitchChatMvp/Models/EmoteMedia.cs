using System.Windows.Media;

namespace TwitchChatMvp.Models;

public sealed record EmoteMedia(IReadOnlyList<ImageSource> Frames, IReadOnlyList<TimeSpan> FrameDelays)
{
    public ImageSource? FirstFrame => Frames.Count > 0 ? Frames[0] : null;
    public bool IsAnimated => Frames.Count > 1;
}
