using Avalonia.Media.Imaging;

namespace WitherChat.Desktop.Services;

public sealed class ChatImageMedia(
    IReadOnlyList<Bitmap> frames,
    IReadOnlyList<TimeSpan> frameDelays) : IDisposable
{
    public IReadOnlyList<Bitmap> Frames { get; } = frames;
    public IReadOnlyList<TimeSpan> FrameDelays { get; } = frameDelays;
    public Bitmap? FirstFrame => Frames.Count > 0 ? Frames[0] : null;
    public bool IsAnimated => Frames.Count > 1;

    public void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
    }
}
